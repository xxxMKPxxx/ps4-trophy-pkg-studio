/*
 * Trophy Unlocker（独立 pkg 版，带界面）
 *
 * 启动就在系统奖杯管理器里调 sceNpTrophyCreateContext / UnlockTrophy
 * （真解锁，不是 Apollo 那种只改本地显示的 fake unlock）。
 *
 * 界面：手选奖杯按 X 解锁，L1 + ○ 一键解锁（按顺序表，带进度和倒计时）。
 *
 * 顺序表：/app0/trophy_order.csv（PkgStudio 打包时塞进去）
 *   一行一个：奖杯ID,延迟秒    # 注释
 *   没有这个文件 → 默认顺序（1..N-1，0 号白金最后），每个间隔 1 秒
 *
 * 日志：/data/trophy_unlocker.log（写不进去退 /user/data）
 *
 * 调用顺序照 OpenOrbis 官方 samples/trophies：
 *   UserService 初始化 → 取用户 → 加载 NP_TROPHY sysmodule →
 *   CreateContext → CreateHandle → RegisterContext
 */
#include <stdarg.h>
#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>
#include <stdio.h>
#include <string.h>
#include <fcntl.h>

#include <orbis/libkernel.h>
#include <orbis/Sysmodule.h>
#include <orbis/UserService.h>
#include <orbis/NpTrophy.h>
#include <orbis/SystemService.h>

#include "gfx.h"
#include "font.h"
#include "log.h"
#include "ui.h"

#define LOG_PATH "/data/trophy_unlocker.log"
#define LOG_PATH2 "/user/data/trophy_unlocker.log"
#define ORDER_PATH "/app0/trophy_order.csv"
#define ORDER_PATH2 "/app0/sce_sys/trophy_order.csv"
#define FONT_PATH "/app0/font.ttf"
#define GAMES_PATH "/app0/games.csv"
#define NPBIND_PATH "/app0/sce_sys/npbind.dat"
#define NPBIND_PATH2 "/app0/npbind.dat"
#define MAX_TROPHIES 512
#define MAX_ORDER 512
#define MAX_GAMES 8
#define DEFAULT_DELAY_SEC 1
#define FAIL_HOLD_SEC 10

/* npbind.dat 里可以挂多个 NPCOMMID（合集类游戏，如 Uncharted 三部曲），
   每块 384B，块内 +16 是 NP Service Label 的 ASCII 数字。
   sceNpTrophyCreateContext 的第 3 个参数就是它：写死 0 会让 label=1/2/3
   的合集稳定返回 0x80551626（请求了不存在的 service label）。 */
typedef struct {
    uint32_t label;          /* 数值型：'3' -> 3 */
    uint32_t label_ascii;    /* 兜底：万一内部按 ASCII 取，试 0x33 */
    char     commid[16];     /* NPWRxxxxx_00，UI 上用来区分是哪一作 */
} np_game_t;

static np_game_t g_games[MAX_GAMES];
static int       g_game_count;
static int       g_game_cur;
static uint32_t  g_label;
static int       g_probe;        /* 清单和 npbind 都读不到时，启动时自动探测 label */

static int32_t g_ctx = -1;
static int32_t g_hdl = -1;
static int g_uid;

/* 奖杯缓存：GetTrophyInfo 是系统调用，绝不能每帧都调 */
static char    g_name[MAX_TROPHIES][128];
static uint8_t g_unlocked[MAX_TROPHIES];
static int     g_count;
static int     s_boot_font = -1;   /* 启动画面字体 slot，失败提示也用它 */

static int g_order_id[MAX_ORDER];
static int g_order_delay[MAX_ORDER];
static int g_order_len;

/* ---------------- 日志 ---------------- */

static int g_logfd = -1;

void log_open(void)
{
    g_logfd = sceKernelOpen(LOG_PATH, O_WRONLY | O_CREAT | O_APPEND, 0777);
    if (g_logfd < 0)
        g_logfd = sceKernelOpen(LOG_PATH2, O_WRONLY | O_CREAT | O_APPEND, 0777);
}

void log_str(const char *s)
{
    if (g_logfd < 0 || !s)
        return;
    sceKernelWrite(g_logfd, s, (size_t)strlen(s));
    /* 每行立即落盘：崩溃时最后一行就是崩在哪一步，别让缓存吞掉线索 */
    sceKernelFsync(g_logfd);
}

void log_int(const char *label, int v)
{
    char buf[80];
    char num[16];
    int n = 0, i = 0;

    if (v < 0) {
        num[n++] = '-';
        v = -v;
    }
    if (v == 0)
        num[n++] = '0';
    while (v > 0 && n < 15) {
        num[n++] = (char)('0' + (v % 10));
        v /= 10;
    }

    while (label[i] && i < 40)
        buf[i] = label[i], i++;
    for (int k = n - 1; k >= 0; k--)
        buf[i++] = num[k];
    buf[i++] = '\n';
    buf[i] = 0;

    log_str(buf);
}

void log_hex(const char *label, uint32_t v)
{
    static const char *H = "0123456789abcdef";
    char buf[80];
    int i = 0;

    while (label[i] && i < 40)
        buf[i] = label[i], i++;
    buf[i++] = '0';
    buf[i++] = 'x';
    for (int shift = 28; shift >= 0; shift -= 4)
        buf[i++] = H[(v >> shift) & 0xF];
    buf[i++] = '\n';
    buf[i] = 0;

    log_str(buf);
}

void log_line(const char *fmt, ...)
{
    char buf[256];
    va_list ap;

    va_start(ap, fmt);
    vsnprintf(buf, sizeof(buf), fmt, ap);
    va_end(ap);

    log_str(buf);
    log_str("\n");
    if (g_logfd >= 0)
        sceKernelFsync(g_logfd);
}

static void boot_frame(const char *msg, int slot);

/* PS4 的 big app **不能从 main 返回**。
   日志显示所有清理都成功、完整走到 === end ===，用户仍在退出时看到
   CE-34878-0 —— 崩的是 libc 的 exit / 内核回收进程这一步，不是我们的代码。
   OpenOrbis 官方 samples/trophies 结尾干脆是 for(;;) 从不返回，就是这个原因。

   正确退出是让 shell 来关应用：
       sceSystemServiceKillApp(appid, -1, 0, 0)
   最后一个 0 = core_dump=0，不生成崩溃报告（hbldr / Itemzflow 同款调用）。
   注意 KillApp 是异步的，调用后绝不能 return，否则又走回 libc exit。

   bye_line != NULL：退出前画一帧告别；NULL：保留当前画面（错误提示）。 */
static void app_exit(const char *bye_line)
{
    if (bye_line && s_boot_font >= 0)
        boot_frame(bye_line, s_boot_font);

    /* 刚解锁过奖杯时，奖杯服务还在异步写盘；给点时间落盘再退出 */
    log_str("exit: drain trophy service\n");
    sceKernelUsleep(1500000);

    log_str("=== end ===\n");
    if (g_logfd >= 0)
        sceKernelFsync(g_logfd);

    int32_t appid = sceSystemServiceGetAppIdOfBigApp();
    log_int("big app id ", (int)appid);

    if (appid > 0) {
        int r = sceSystemServiceKillApp((uint32_t)appid, -1, 0, 0);
        log_hex("KillApp ", (uint32_t)r);
        sceKernelSleep(3);              /* 等 shell 处理关闭请求 */
    } else {
        log_str("KillApp skipped (no app id)\n");
    }

    /* 没被系统关掉：不 return、不清理画面，常驻显示提示，
       让用户在 PS 菜单里点「关闭应用程序」—— 这条路径同样不会报错。 */
    if (s_boot_font >= 0)
        boot_frame("Trophy Unlocker  请按 PS 键 → 关闭应用程序", s_boot_font);
    log_str("exit: waiting for user close\n");
    if (g_logfd >= 0)
        sceKernelFsync(g_logfd);

    for (;;)
        sceKernelSleep(1);
}

/* 所有失败分支也必须走 app_exit，不能 return */
static void hold_and_exit(const char *msg)
{
    if (msg && s_boot_font >= 0)
        boot_frame(msg, s_boot_font);
    if (g_logfd >= 0)
        sceKernelFsync(g_logfd);
    sceKernelSleep(FAIL_HOLD_SEC);
    log_str("=== end (failed) ===\n");
    app_exit(NULL);                     /* 不返回 */
}

/* 奖杯 context/handle 成对创建。第 3 个参数是 NP Service Label，
   必须传 npbind 里真实存在的那个，否则 RegisterContext 直接被拒。 */
static int create_pair(int uid, uint32_t label)
{
    int r;

    g_ctx = -1;
    g_hdl = -1;

    r = sceNpTrophyCreateContext(&g_ctx, uid, label, 0);
    log_hex("CreateContext ", (uint32_t)r);
    if (r != 0)
        return r;

    r = sceNpTrophyCreateHandle(&g_hdl);
    log_hex("CreateHandle ", (uint32_t)r);
    if (r != 0)
        g_hdl = -1;
    return r;
}

static void destroy_pair(void)
{
    if (g_hdl >= 0) {
        sceNpTrophyDestroyHandle(g_hdl);
        g_hdl = -1;
    }
    if (g_ctx >= 0) {
        sceNpTrophyDestroyContext(g_ctx);
        g_ctx = -1;
    }
}

/* ---------------- 顺序表 ---------------- */

static int read_all(const char *path, char *buf, int cap)
{
    int fd = sceKernelOpen(path, O_RDONLY, 0);
    int total = 0;

    if (fd < 0) {
        /* 记录真实错误码：npbind.dat 是受保护的系统文件，big app open 会失败 */
        log_str("open fail: ");
        log_str(path);
        log_str(" -> ");
        log_hex("", (uint32_t)fd);
        return -1;
    }

    while (total < cap - 1) {
        long n = sceKernelRead(fd, buf + total, (size_t)(cap - 1 - total));
        if (n <= 0)
            break;
        total += (int)n;
    }
    buf[total] = 0;
    sceKernelClose(fd);
    return total;
}

static int atoi_simple(const char *p)
{
    int v = 0, neg = 0;

    while (*p == ' ' || *p == '\t')
        p++;
    if (*p == '-') {
        neg = 1;
        p++;
    }
    while (*p >= '0' && *p <= '9') {
        v = v * 10 + (*p - '0');
        p++;
    }
    return neg ? -v : v;
}

static void load_order(void)
{
    static char buf[8192];
    int got = read_all(ORDER_PATH, buf, sizeof(buf));
    char *p = buf;

    g_order_len = 0;

    if (got < 0)
        got = read_all(ORDER_PATH2, buf, sizeof(buf));

    if (got < 0) {
        log_str("order: 包里没有 trophy_order.csv，用默认顺序\n");
        return;
    }

    while (*p && g_order_len < MAX_ORDER) {
        char *line = p;
        char *s;
        int id, delay;

        while (*p && *p != '\n')
            p++;
        if (*p == '\n')
            *p++ = 0;

        s = line;
        while (*s == ' ' || *s == '\t')
            s++;
        if (*s == '#' || *s == 0 || *s == '\r')
            continue;

        id = atoi_simple(s);
        delay = DEFAULT_DELAY_SEC;

        {
            char *comma = strchr(s, ',');
            if (comma)
                delay = atoi_simple(comma + 1);
        }

        if (id < 0 || id > 4095)
            continue;
        if (delay < 0)
            delay = 0;
        if (delay > 600)
            delay = 600;

        g_order_id[g_order_len] = id;
        g_order_delay[g_order_len] = delay;
        g_order_len++;
    }

    log_int("order entries ", g_order_len);
}

/* ---------------- npbind 多奖杯集解析 ---------------- */

/* 实测格式（CUSA01399 / CUSA00066 / CUSA02104）：
     0x00..0x83  文件头，@0x1C 是大端块数（1 / 2 / 3）
     0x84 起     每块 384B：+0 = "NPWRxxxxx_00"，+12 = 00 11 00 0c，
                 +16 = service label 的 ASCII 数字（'0' / '1' / '2' / '3'）
     末尾 16B    校验，不算块
   普通游戏只有一块 label='0'；Uncharted 三部曲是 '1'/'2'/'3'（没有 0）。 */
/* 优先读打包器生成的 /app0/games.csv。
   npbind.dat 是受保护的系统文件：gp4 会把它打进 sce_sys/，系统 trophy 服务能读，
   但 big app 运行时 open("/app0/sce_sys/npbind.dat") 会失败（实测 17:41 那个包），
   所以由 PkgStudio 在打包时把 label + NPCOMMID 写成 app0 根目录的普通文本。 */
static int parse_games_csv(void)
{
    static char buf[2048];
    int got = read_all(GAMES_PATH, buf, sizeof(buf));
    char *p = buf;
    int n = 0;

    if (got < 0)
        return 0;

    while (*p && n < MAX_GAMES) {
        char *line = p, *c1, *c2;
        int label, ascii, i;

        while (*p && *p != '\n')
            p++;
        if (*p == '\n')
            *p++ = 0;
        for (i = 0; line[i]; i++) {
            if (line[i] == '\r') {
                line[i] = 0;
                break;
            }
        }

        if (line[0] == '#' || line[0] == 0)
            continue;
        c1 = strchr(line, ',');
        if (!c1)
            continue;
        c2 = strchr(c1 + 1, ',');
        if (!c2)
            continue;

        label = atoi_simple(line);
        ascii = atoi_simple(c1 + 1);
        if (label < 0 || label > 255 || ascii < 0 || ascii > 255)
            continue;

        memset(&g_games[n], 0, sizeof(g_games[0]));
        g_games[n].label = (uint32_t)label;
        g_games[n].label_ascii = (uint32_t)ascii;
        strncpy(g_games[n].commid, c2 + 1, sizeof(g_games[0].commid) - 1);
        g_games[n].commid[sizeof(g_games[0].commid) - 1] = 0;
        n++;
    }

    return n;
}

/* 实测格式（CUSA01399 / CUSA00066 / CUSA02104）：
     0x00..0x83  文件头，@0x1C 是大端块数（1 / 2 / 3）
     0x84 起     每块 384B：+0 = "NPWRxxxxx_00"，+12 = 00 11 00 0c，
                 +16 = service label 的 ASCII 数字（'0' / '1' / '2' / '3'）
     末尾 16B    校验，不算块
   普通游戏只有一块 label='0'；Uncharted 三部曲是 '1'/'2'/'3'（没有 0）。 */
static void parse_npbind(void)
{
    static unsigned char buf[8192];
    int got;
    int blocks = 0;
    int n;

    g_game_count = 0;
    g_game_cur = 0;
    g_label = 0;
    g_probe = 0;

    n = parse_games_csv();
    if (n > 0) {
        g_game_count = n;
        log_int("games.csv games ", n);
        for (int i = 0; i < n; i++)
            log_line("game %d = %s label=%u ascii=0x%02x", i,
                     g_games[i].commid, (unsigned)g_games[i].label,
                     (unsigned)g_games[i].label_ascii);
        return;
    }

    log_str("games.csv 读不到，回退解析 npbind.dat\n");
    got = read_all(NPBIND_PATH, (char *)buf, sizeof(buf));
    if (got < 0)
        got = read_all(NPBIND_PATH2, (char *)buf, sizeof(buf));

    if (got >= 148) {
        blocks = ((int)buf[28] << 24) | ((int)buf[29] << 16) |
                 ((int)buf[30] << 8) | (int)buf[31];
        if (blocks < 1 || blocks > MAX_GAMES)
            blocks = 1;

        for (int i = 0; i < blocks && g_game_count < MAX_GAMES; i++) {
            int off = 132 + i * 384;
            unsigned char c;
            uint32_t lab;

            if (off + 17 > got)
                break;
            if (memcmp(buf + off, "NPWR", 4) != 0)
                continue;

            c = buf[off + 16];
            lab = (c >= '0' && c <= '9') ? (uint32_t)(c - '0') : (uint32_t)i;

            memset(&g_games[g_game_count], 0, sizeof(g_games[0]));
            g_games[g_game_count].label = lab;
            g_games[g_game_count].label_ascii = (uint32_t)c;
            memcpy(g_games[g_game_count].commid, buf + off, 12);
            g_game_count++;
            log_line("npbind game %d = %s label=%u ascii=0x%02x",
                     g_game_count - 1, g_games[g_game_count - 1].commid,
                     (unsigned)lab, (unsigned)c);
        }
    } else {
        log_str("npbind: 读不到文件\n");
    }

    if (g_game_count == 0) {
        /* 两个来源都读不到（旧包 / 精简模板）：不知道有哪些 label，
           启动时让 register_game 依次探测 0..5 与 ASCII 值，找到能注册的就用，
           UI 按单个奖杯集显示，不会再卡死在写死的 label 0。 */
        memset(&g_games[0], 0, sizeof(g_games[0]));
        strcpy(g_games[0].commid, "auto");
        g_games[0].label_ascii = '0';
        g_game_count = 1;
        g_probe = 1;
        log_str("清单与 npbind 都读不到，启动时自动探测 service label\n");
    }

    log_int("npbind games ", g_game_count);
}

/* ---------------- 奖杯 API（给界面用） ---------------- */

int api_count(void)
{
    return g_count;
}

int api_user_id(void)
{
    return g_uid;
}

int api_order_len(void)
{
    return g_order_len;
}

int api_order_get(int i, int *id, int *delay_sec)
{
    if (i < 0 || i >= g_order_len)
        return -1;

    if (id)
        *id = g_order_id[i];
    if (delay_sec)
        *delay_sec = g_order_delay[i];
    return 0;
}

static int get_info(int id, OrbisNpTrophyDetails *d, OrbisNpTrophyData *dt)
{
    memset(d, 0, sizeof(*d));
    memset(dt, 0, sizeof(*dt));
    d->size = sizeof(*d);
    dt->size = sizeof(*dt);

    return sceNpTrophyGetTrophyInfo(g_ctx, g_hdl, id, d, dt);
}

void api_refresh(void)
{
    for (int id = 0; id < g_count; id++) {
        OrbisNpTrophyDetails d;
        OrbisNpTrophyData dt;

        if (get_info(id, &d, &dt) != 0) {
            g_name[id][0] = 0;
            g_unlocked[id] = 0;
            continue;
        }

        d.TrophyName[sizeof(d.TrophyName) - 1] = 0;
        strncpy(g_name[id], d.TrophyName, sizeof(g_name[id]) - 1);
        g_name[id][sizeof(g_name[id]) - 1] = 0;
        g_unlocked[id] = dt.IsUnlocked ? 1 : 0;
    }
}

int api_get(int id, char *name, int cap, int *unlocked)
{
    if (id < 0 || id >= g_count)
        return -1;

    if (name && cap > 0) {
        strncpy(name, g_name[id], (size_t)cap - 1);
        name[cap - 1] = 0;
    }
    if (unlocked)
        *unlocked = g_unlocked[id];

    return 0;
}

int api_unlock(int id)
{
    int plat = -1;
    int rc;

    if (id < 0 || id >= g_count)
        return -1;

    rc = sceNpTrophyUnlockTrophy(g_ctx, g_hdl, id, &plat);
    if (rc == 0)
        g_unlocked[id] = 1;

    return rc;
}

/* ---------------- 奖杯集注册 / 切换 ---------------- */

static void recount_trophies(void)
{
    int count = 0, miss = 0;

    for (int id = 0; id < MAX_TROPHIES && miss < 5; id++) {
        OrbisNpTrophyDetails d;
        OrbisNpTrophyData dt;

        if (get_info(id, &d, &dt) == 0) {
            count = id + 1;
            miss = 0;
        } else {
            miss++;
        }
    }

    g_count = count;
    log_int("trophy count ", g_count);
}

/* 用某个 label 建一对 context/handle 并注册。
   成功：旧 context 故意不销毁（RegisterContext 之后销毁有崩溃风险），
         交给进程退出时系统回收，只把全局句柄换成新的。
   失败：销毁这次新建、未注册成功的那对，恢复调用前的句柄。 */
static int try_label(uint32_t label)
{
    int32_t old_ctx = g_ctx, old_hdl = g_hdl;
    int rc;

    log_hex("label try ", label);
    rc = create_pair(g_uid, label);
    if (rc == 0)
        rc = sceNpTrophyRegisterContext(g_ctx, g_hdl, 0);
    log_hex("  RegisterContext ", (uint32_t)rc);

    if (rc != 0) {
        destroy_pair();
        g_ctx = old_ctx;
        g_hdl = old_hdl;
        return rc;
    }

    g_label = label;
    return 0;
}

/* 注册第 idx 个奖杯集。
   label 官方含义没公开文档，主候选按数值（1/2/3），另备 ASCII 值（0x31…）
   和 0；实测哪个能注册成功就用哪个，并把成功的 label 记进日志。
   verbose=1 是启动注册（保留 6 次重试应对间歇的 0x805516f4）。 */
static int register_game(int idx, int verbose)
{
    uint32_t cand[16];
    int nc = 0, last = -1;
    char msg[192];

    if (idx < 0 || idx >= g_game_count)
        return -1;

    if (g_probe && verbose) {
        /* 没有任何清单：依次探测常见 label（数值 0..5 + 对应 ASCII 值），
           第一个能注册成功的生效。只在启动时做，切换仍走正常候选。 */
        for (uint32_t l = 0; l < 6; l++)
            cand[nc++] = l;
        for (uint32_t l = 0; l < 6; l++)
            cand[nc++] = (uint32_t)('0' + l);
        log_str("probe: 无清单，逐个尝试 service label\n");
    } else {
        cand[nc++] = g_games[idx].label;
        if (g_games[idx].label_ascii != cand[0])
            cand[nc++] = g_games[idx].label_ascii;
        if (cand[0] != 0 && g_games[idx].label_ascii != 0)
            cand[nc++] = 0;
    }

    for (int c = 0; c < nc; c++) {
        int tries = (c == 0 && verbose && !g_probe) ? 6 : 1;

        for (int a = 1; a <= tries; a++) {
            last = try_label(cand[c]);
            if (last == 0) {
                g_game_cur = idx;
                log_int("active game ", idx);
                log_hex("active label ", g_label);
                return 0;
            }

            log_int("register retry ", a);
            if (verbose) {
                snprintf(msg, sizeof(msg),
                         "Trophy Unlocker  奖杯服务未就绪 0x%08x，重试 %d/%d…",
                         (uint32_t)last, a + 1, tries);
                boot_frame(msg, s_boot_font);
            }
            sceKernelUsleep(a < 3 ? 700000 : 1500000);
        }
    }

    return last ? last : -1;
}

int api_game_count(void)
{
    return g_game_count;
}

int api_game_cur(void)
{
    return g_game_cur;
}

int api_game_info(int idx, char *commid, int cap, uint32_t *label)
{
    if (idx < 0 || idx >= g_game_count)
        return -1;

    if (commid && cap > 0) {
        strncpy(commid, g_games[idx].commid, (size_t)cap - 1);
        commid[cap - 1] = 0;
    }
    if (label)
        *label = g_games[idx].label;
    return 0;
}

/* 界面里选中另一个奖杯集时调用：重新注册 + 重数奖杯 + 刷新名字缓存。
   失败时 try_label 已把句柄恢复成切换前那一对，界面照常可用。 */
int api_switch_game(int idx)
{
    int rc;

    if (idx < 0 || idx >= g_game_count || idx == g_game_cur)
        return -1;

    log_int("switch to game ", idx);
    rc = register_game(idx, 0);
    if (rc != 0) {
        log_hex("switch failed ", (uint32_t)rc);
        return rc;
    }

    recount_trophies();
    api_refresh();
    log_int("switch ok trophies ", g_count);
    return 0;
}

/* ---------------- 启动 ---------------- */

#define BOOT_BG   (0x80000000u | (16u << 16) | (18u << 8) | 24u)
#define BOOT_FG   (0x80000000u | (232u << 16) | (236u << 8) | 244u)
#define BOOT_BAR  (0x80000000u | (64u << 16) | (156u << 8) | 255u)

/* 启动阶段先顶掉系统画面；之后每次长操作前都可以再刷一次 */
static void boot_frame(const char *msg, int slot)
{
    gfx_clear(BOOT_BG);
    gfx_fill(0, 0, GFX_WIDTH, 10, BOOT_BAR);
    if (slot >= 0 && msg)
        font_draw_fit(slot, 120, 120, GFX_WIDTH - 240, msg, BOOT_FG);
    gfx_present();
}

int main(void)
{
    int rc;
    int uid = 0;

    log_open();
    log_str("=== trophy unlocker (pkg) start ===\n");
    if (g_logfd < 0) {
        hold_and_exit("日志无法写入 /data");
        return 1;
    }

    /* 0. 立刻接管显示 —— 必须排在所有慢操作之前。
       这个 pkg 带原游戏的 trophy00.trp，PS4 启动时会先显示奖杯启动画面；
       应用不主动 HideSplashScreen、又迟迟不提交首帧，系统画面就一直盖着，
       用户看到"卡在奖杯画面进不了 UI"，最后系统按启动超时报 CE-34878-0。 */
    rc = gfx_init();
    log_hex("gfx_init ", (uint32_t)rc);
    if (rc != 0) {
        hold_and_exit("显示初始化失败，无法启动");
        return 1;
    }

    boot_frame(NULL, -1);                  /* 首帧：纯色，先把系统画面顶掉 */
    log_str("first frame submitted\n");

    /* VideoOut 打开并提交首帧之后再通知系统收起 splash，顺序反了不生效 */
    rc = sceSystemServiceHideSplashScreen();
    log_hex("HideSplashScreen ", (uint32_t)rc);

    rc = font_init(FONT_PATH);
    log_hex("font_init ", (uint32_t)rc);
    if (rc != 0) {
        log_str("字体加载失败（/app0/font.ttf）\n");
        hold_and_exit("字体加载失败：包里缺少 font.ttf");
        return 1;
    }
    s_boot_font = font_size(40);
    log_int("boot font slot ", s_boot_font);
    boot_frame("Trophy Unlocker  正在读取奖杯…", s_boot_font);

    /* 1. UserService */
    rc = sceUserServiceInitialize(NULL);
    log_hex("sceUserServiceInitialize ", (uint32_t)rc);

    rc = sceUserServiceGetInitialUser(&uid);
    log_hex("GetInitialUser ", (uint32_t)rc);

    if (uid <= 0) {
        uid = 0;
        rc = sceUserServiceGetForegroundUser(&uid);
        log_hex("GetForegroundUser ", (uint32_t)rc);
    }

    if (uid <= 0) {
        OrbisUserServiceLoginUserIdList list;

        memset(&list, 0, sizeof(list));
        rc = sceUserServiceGetLoginUserIdList(&list);
        log_hex("GetLoginUserIdList ", (uint32_t)rc);

        for (int i = 0; i < ORBIS_USER_SERVICE_MAX_LOGIN_USERS; i++) {
            if (list.userId[i] > 0) {
                uid = list.userId[i];
                break;
            }
        }
    }

    log_int("user id ", uid);
    if (uid <= 0) {
        log_str("拿不到用户，放弃\n");
        hold_and_exit("拿不到登录用户，无法启动");
        return 1;
    }
    g_uid = uid;

    /* 2. 系统奖杯模块 */
    rc = sceSysmoduleLoadModule(ORBIS_SYSMODULE_NP_TROPHY);
    log_hex("LoadModule NP_TROPHY ", (uint32_t)rc);

    /* 2.1 解析 npbind 的 service label 并注册 context。
       合集类游戏（Uncharted 三部曲、火影三部曲…）npbind 里挂多个 NPCOMMID，
       label 不一定从 0 开始；写死 0 会稳定返回 0x80551626。
       注册仍带重试，排除奖杯服务短暂未就绪的间歇性 0x805516f4。 */
    parse_npbind();
    rc = register_game(0, 1);

    if (rc != 0) {
        char msg[192];
        snprintf(msg, sizeof(msg),
                 "Trophy Unlocker  奖杯注册失败 0x%08x，请关闭本程序后重新打开",
                 (uint32_t)rc);
        log_str("register gave up\n");
        hold_and_exit(msg);
        return 1;
    }

    /* 3. 数奖杯 */
    recount_trophies();

    if (g_count <= 0) {
        log_str("这个包没有奖杯集\n");
        hold_and_exit("这个包里没有奖杯集（trophy00.trp 无效）");
        return 1;
    }

    api_refresh();
    load_order();
    boot_frame("Trophy Unlocker  就绪", s_boot_font);

    log_str("entering ui\n");
    ui_run();
    log_str("ui returned\n");

    /* 5. 退出：不主动销毁奖杯 context（RegisterContext 是异步的，交给系统回收），
       也**不从 main 返回** —— 由 app_exit() 请求 shell 关闭本应用，
       之后常驻等待，避免 libc exit 触发 CE-34878-0。 */
    log_str("skip destroy (system reclaims)\n");

    app_exit("Trophy Unlocker  正在退出…");

    return 0;                            /* 不可达，app_exit 永不返回 */
}
