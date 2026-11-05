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
#define MAX_TROPHIES 512
#define MAX_ORDER 512
#define DEFAULT_DELAY_SEC 1
#define FAIL_HOLD_SEC 10

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

/* 奖杯 context/handle 成对创建，方便注册失败时整体重建 */
static int create_pair(int uid)
{
    int r = sceNpTrophyCreateContext(&g_ctx, uid, 0, 0);
    log_hex("CreateContext ", (uint32_t)r);
    if (r != 0)
        return r;

    r = sceNpTrophyCreateHandle(&g_hdl);
    log_hex("CreateHandle ", (uint32_t)r);
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

    if (fd < 0)
        return -1;

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

    /* 2.1 注册 context（带重试）
       实测会间歇性返回 0x805516f4：上一次进程被强杀、或奖杯服务还在写
       上一次解锁的数据时，注册会被拒。原来直接 hold_and_exit，画面停在
       「正在读取奖杯…」看起来像卡死。现在最多试 6 次，每次把状态画出来；
       第 3 次起连 context 一起重建，排除「残留上下文」这一类原因。 */
    {
        const int TRIES = 6;
        char msg[192];

        rc = create_pair(uid);

        for (int attempt = 1; attempt <= TRIES; attempt++) {
            if (rc == 0) {
                rc = sceNpTrophyRegisterContext(g_ctx, g_hdl, 0);
                log_hex("RegisterContext ", (uint32_t)rc);
            }
            if (rc == 0)
                break;
            if (attempt == TRIES)
                break;

            log_int("register retry ", attempt);
            snprintf(msg, sizeof(msg),
                     "Trophy Unlocker  奖杯服务未就绪 0x%08x，重试 %d/%d…",
                     (uint32_t)rc, attempt + 1, TRIES);
            boot_frame(msg, s_boot_font);
            sceKernelUsleep(attempt < 3 ? 700000 : 1500000);

            if (attempt >= 3) {
                /* 销毁的是「注册失败、没有异步解锁在跑」的 context，
                   和进程退出时销毁（会崩）不是一回事 */
                destroy_pair();
                rc = create_pair(uid);
            } else {
                rc = 0;                /* 保留 context，直接再注册一次 */
            }
        }
    }

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
