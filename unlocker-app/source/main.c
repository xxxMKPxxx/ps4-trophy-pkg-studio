/*
 * Trophy Unlocker（独立 pkg 版）—— 在 Unlocker 的 PKG 里运行
 *
 * 启动就在系统奖杯管理器里调 sceNpTrophyCreateContext / UnlockTrophy
 * （真解锁，不是 Apollo 那种只改本地显示的 fake unlock）。
 * 不依赖 GoldHEN 插件、不需要手柄交互 —— 启动就按顺序解锁，跑完自己退出。
 *
 * 顺序表：/app0/trophy_order.csv（打包时塞进包里）
 *   一行一个：奖杯ID,延迟毫秒    # 注释
 *   没有这个文件 → 按奖杯 ID 0..N-1 顺序解锁，无延迟
 * 断点续解：已经解锁的跳过，所以再启动一次会从剩下的继续。
 *
 * 日志：/data/trophy_unlocker.log（FTP 可直接看），写不进去退到 /user/data
 *
 * 调用顺序照 OpenOrbis 官方 samples/trophies：UserService 初始化 → 取用户 →
 * 加载 NP_TROPHY sysmodule → CreateContext → CreateHandle → RegisterContext。
 * 少一步都可能在上层服务没就绪时直接崩（CE-34878-0）。
 * 每一步都写日志，最后一行就是崩在哪。
 */
#include <stdint.h>
#include <stddef.h>
#include <stdbool.h>
#include <string.h>
#include <fcntl.h>

#include <orbis/libkernel.h>
#include <orbis/Sysmodule.h>
#include <orbis/UserService.h>
#include <orbis/NpTrophy.h>

#define LOG_PATH "/data/trophy_unlocker.log"
#define LOG_PATH2 "/user/data/trophy_unlocker.log"
#define ORDER_PATH "/app0/trophy_order.csv"
#define ORDER_PATH2 "/app0/sce_sys/trophy_order.csv"
#define MAX_TROPHIES 512
#define MAX_ORDER 512
#define DEFAULT_DELAY_MS 1500
#define FAIL_HOLD_SEC 15

static int32_t g_ctx = -1;
static int32_t g_hdl = -1;

/* ---------------- 日志 ---------------- */

static int g_logfd = -1;

static void log_open(void)
{
    g_logfd = sceKernelOpen(LOG_PATH, O_WRONLY | O_CREAT | O_APPEND, 0777);
    if (g_logfd < 0)
        g_logfd = sceKernelOpen(LOG_PATH2, O_WRONLY | O_CREAT | O_APPEND, 0777);
}

static void log_str(const char *s)
{
    if (g_logfd < 0 || !s)
        return;
    sceKernelWrite(g_logfd, s, (size_t)strlen(s));
}

static void log_int(const char *label, int v)
{
    char buf[64];
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

static void log_hex(const char *label, uint32_t v)
{
    static const char *H = "0123456789abcdef";
    char buf[64];
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

/* 失败时别立刻退出：留点时间把日志刷下去 */
static void hold_and_exit(void)
{
    if (g_logfd >= 0)
        sceKernelFsync(g_logfd);
    sceKernelSleep(FAIL_HOLD_SEC);
}

/* ---------------- 小工具 ---------------- */

static int read_all(const char *path, char *buf, int cap)
{
    int fd = sceKernelOpen(path, O_RDONLY, 0);
    if (fd < 0)
        return -1;

    int total = 0;
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

/* 解析顺序表；返回条目数（0 = 没有文件或全空） */
static int load_order(int *ids, int *delays, int max)
{
    static char buf[8192];
    int got = read_all(ORDER_PATH, buf, sizeof(buf));
    if (got < 0)
        got = read_all(ORDER_PATH2, buf, sizeof(buf));

    if (got < 0) {
        log_str("order: 包里没有 trophy_order.csv，按默认顺序\n");
        return 0;
    }

    int n = 0;
    char *p = buf;

    while (*p && n < max) {
        char *line = p;
        while (*p && *p != '\n')
            p++;
        if (*p == '\n')
            *p++ = 0;

        char *s = line;
        while (*s == ' ' || *s == '\t')
            s++;
        if (*s == '#' || *s == 0 || *s == '\r')
            continue;

        int id = atoi_simple(s);
        int delay = DEFAULT_DELAY_MS;

        char *comma = strchr(s, ',');
        if (comma)
            delay = atoi_simple(comma + 1);

        if (id < 0 || id > 4095)
            continue;
        if (delay < 0)
            delay = 0;
        if (delay > 60000)
            delay = 60000;

        ids[n] = id;
        delays[n] = delay;
        n++;
    }

    log_int("order: 读到条目 ", n);
    return n;
}

/* ---------------- 奖杯 ---------------- */

static int get_info(int id, OrbisNpTrophyDetails *d, OrbisNpTrophyData *dt)
{
    memset(d, 0, sizeof(*d));
    memset(dt, 0, sizeof(*dt));
    d->size = sizeof(*d);
    dt->size = sizeof(*dt);

    return sceNpTrophyGetTrophyInfo(g_ctx, g_hdl, id, d, dt);
}

/* 注册是异步的：等 0 号奖杯能读到，说明 context 真正可用了 */
static int wait_registered(void)
{
    OrbisNpTrophyDetails d;
    OrbisNpTrophyData dt;

    for (int i = 0; i < 100; i++) {
        if (get_info(0, &d, &dt) == 0)
            return 0;
        sceKernelUsleep(100000);
    }
    return -1;
}

static int trophy_count(void)
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
    return count;
}

static int is_unlocked(int id)
{
    OrbisNpTrophyDetails d;
    OrbisNpTrophyData dt;

    if (get_info(id, &d, &dt) != 0)
        return 0;

    return dt.IsUnlocked ? 1 : 0;
}

int main(void)
{
    static int ids[MAX_ORDER];
    static int delays[MAX_ORDER];

    log_open();
    log_str("=== trophy unlocker (pkg) start ===\n");
    if (g_logfd < 0) {
        hold_and_exit();
        return 1;
    }

    /* 1. UserService：官方 sample 的第一件事；已初始化会返回 0x80960003，可忽略 */
    int rc = sceUserServiceInitialize(NULL);
    log_hex("sceUserServiceInitialize ", (uint32_t)rc);

    int uid = 0;
    rc = sceUserServiceGetInitialUser(&uid);
    log_int("user id ", uid);
    if (rc != 0 || uid == 0) {
        rc = sceUserServiceGetForegroundUser(&uid);
        log_hex("GetForegroundUser ", (uint32_t)rc);
        log_int("user id ", uid);
    }
    if (uid == 0) {
        log_str("拿不到用户，放弃\n");
        hold_and_exit();
        return 1;
    }

    /* 2. 先加载 NP Trophy sysmodule —— 不加载直接调奖杯 API 会崩 */
    rc = sceSysmoduleLoadModule(ORBIS_SYSMODULE_NP_TROPHY);
    log_hex("LoadModule NP_TROPHY ", (uint32_t)rc);

    log_str("calling CreateContext...\n");
    rc = sceNpTrophyCreateContext(&g_ctx, uid, 0, 0);
    log_hex("CreateContext ", (uint32_t)rc);
    if (rc != 0) {
        hold_and_exit();
        return 1;
    }

    rc = sceNpTrophyCreateHandle(&g_hdl);
    log_hex("CreateHandle ", (uint32_t)rc);
    if (rc != 0) {
        hold_and_exit();
        return 1;
    }

    /* 3. RegisterContext 只有 3 个参数（ctx, handle, unk），官方 sample 传 0。
     *    这一步会读 pkg 里的奖杯包，可能要好几秒。 */
    log_str("calling RegisterContext...\n");
    rc = sceNpTrophyRegisterContext(g_ctx, g_hdl, 0);
    log_hex("RegisterContext ", (uint32_t)rc);
    if (rc != 0) {
        hold_and_exit();
        return 1;
    }

    if (wait_registered() != 0) {
        log_str("注册后读不到奖杯（奖杯集没装上？）\n");
        hold_and_exit();
        return 1;
    }
    log_str("context registered\n");

    sceKernelUsleep(300000);

    int count = trophy_count();
    log_int("trophy count ", count);
    if (count <= 0) {
        log_str("这个包没有奖杯集\n");
        hold_and_exit();
        return 1;
    }

    int n = load_order(ids, delays, MAX_ORDER);
    int ok = 0, skipped = 0;

    if (n > 0) {
        /* 0 号是白金，提前解锁会出错：不管表里怎么写，一律挪到最末尾 */
        int pos = -1;
        for (int i = 0; i < n; i++) {
            if (ids[i] == 0) {
                pos = i;
                break;
            }
        }
        if (pos >= 0) {
            int d = delays[pos];
            for (int i = pos; i < n - 1; i++) {
                ids[i] = ids[i + 1];
                delays[i] = delays[i + 1];
            }
            ids[n - 1] = 0;
            delays[n - 1] = d;
            log_str("order: 0 号（白金）已挪到最后\n");
        }
    } else {
        /* 没有顺序表：先 1..count-1，最后才是 0 号白金 */
        n = 0;
        for (int id = 1; id < count && n < MAX_ORDER; id++) {
            ids[n] = id;
            delays[n] = 0;
            n++;
        }
        if (n < MAX_ORDER) {
            ids[n] = 0;
            delays[n] = 0;
            n++;
        }
        log_str("order: 用默认顺序（0 号白金放最后）\n");
    }

    for (int i = 0; i < n; i++) {
        int id = ids[i];
        if (id >= count)
            continue;

        if (is_unlocked(id)) {
            skipped++;
            continue;
        }

        int plat = -1;
        rc = sceNpTrophyUnlockTrophy(g_ctx, g_hdl, id, &plat);
        if (rc == 0) {
            ok++;
        } else {
            log_int("unlock failed id ", id);
            log_hex("  rc ", (uint32_t)rc);
        }

        if (delays[i] > 0)
            sceKernelUsleep((unsigned int)delays[i] * 1000u);
    }

    log_int("done: unlocked ", ok);
    log_int("done: already had ", skipped);
    log_str("=== end ===\n");

    /* 正常收尾：先销毁 handle/context 再退出 */
    sceNpTrophyDestroyHandle(g_hdl);
    sceNpTrophyDestroyContext(g_ctx);

    if (g_logfd >= 0)
        sceKernelFsync(g_logfd);
    sceKernelUsleep(500000);
    return 0;
}
