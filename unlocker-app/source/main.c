/*
 * Trophy Unlocker（独立 pkg 版）—— 在 Unlocker 的 PKG 里运行
 *
 * 为什么放这里：这个包用自己的 nptitle.dat 冒充目标游戏（每个游戏一个 Title ID），
 * 所以进程的 NP Communication ID 天然就是那个游戏，可以直接调
 * sceNpTrophyCreateContext / UnlockTrophy，走系统奖杯管理器（真解锁）。
 * 不依赖 GoldHEN 插件、不需要手柄交互 —— 启动就按顺序解锁，跑完自己退出。
 *
 * 顺序表：/app0/trophy_order.csv（打包时塞进包里）
 *   一行一个：奖杯ID,延迟毫秒    # 注释
 *   没有这个文件 → 按奖杯 ID 0..N-1 顺序解锁，无延迟
 * 断点续解：已经解锁的跳过，所以再启动一次会从剩下的继续。
 *
 * 日志：/data/trophy_unlocker.log（FTP 可直接看）
 */
#include <stdint.h>
#include <stddef.h>
#include <string.h>
#include <fcntl.h>

#include <orbis/libkernel.h>
#include <orbis/UserService.h>
#include <orbis/NpTrophy.h>

#define LOG_PATH "/data/trophy_unlocker.log"
#define ORDER_PATH "/app0/trophy_order.csv"
#define ORDER_PATH2 "/app0/sce_sys/trophy_order.csv"
#define MAX_TROPHIES 512
#define MAX_ORDER 512
#define DEFAULT_DELAY_MS 1500

/* NpTrophy 的函数用 dlsym 取，避免和头文件里的声明打架 */
typedef int32_t (*fn_create_ctx)(int32_t *, int32_t, uint32_t, uint64_t);
typedef int32_t (*fn_create_hdl)(int32_t *);
typedef int32_t (*fn_reg_ctx)(int32_t, int32_t, void *, void *, uint64_t);
typedef int32_t (*fn_get_info)(int32_t, int32_t, int32_t, OrbisNpTrophyDetails *, OrbisNpTrophyData *);
typedef int32_t (*fn_unlock)(int32_t, int32_t, int32_t, int32_t *);

static fn_create_ctx p_CreateContext;
static fn_create_hdl p_CreateHandle;
static fn_reg_ctx p_RegisterContext;
static fn_get_info p_GetTrophyInfo;
static fn_unlock p_UnlockTrophy;

static int32_t g_ctx = -1;
static int32_t g_hdl = -1;
static volatile int g_registered;

/* ---------------- 日志 ---------------- */

static int g_logfd = -1;

static void log_open(void)
{
    g_logfd = sceKernelOpen(LOG_PATH, O_WRONLY | O_CREAT | O_APPEND, 0777);
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

static void on_registered(int32_t ctx, int32_t hdl, int32_t error, void *arg)
{
    (void)ctx;
    (void)hdl;
    (void)arg;
    g_registered = (error == 0) ? 1 : -1;
}

static int resolve_symbols(void)
{
    int res = 0;
    int lib = sceKernelLoadStartModule("libSceNpTrophy.sprx", 0, NULL, 0, NULL, &res);
    if (lib < 0) {
        log_int("load libSceNpTrophy failed 0x", lib);
        return -1;
    }

    if (sceKernelDlsym(lib, "sceNpTrophyCreateContext", (void **)&p_CreateContext) != 0 ||
        sceKernelDlsym(lib, "sceNpTrophyCreateHandle", (void **)&p_CreateHandle) != 0 ||
        sceKernelDlsym(lib, "sceNpTrophyRegisterContext", (void **)&p_RegisterContext) != 0 ||
        sceKernelDlsym(lib, "sceNpTrophyGetTrophyInfo", (void **)&p_GetTrophyInfo) != 0 ||
        sceKernelDlsym(lib, "sceNpTrophyUnlockTrophy", (void **)&p_UnlockTrophy) != 0) {
        log_str("dlsym NpTrophy failed\n");
        return -1;
    }

    return 0;
}

static int trophy_count(void)
{
    int count = 0, miss = 0;

    for (int id = 0; id < MAX_TROPHIES && miss < 5; id++) {
        OrbisNpTrophyDetails d;
        OrbisNpTrophyData dt;

        memset(&d, 0, sizeof(d));
        memset(&dt, 0, sizeof(dt));
        d.size = sizeof(d);
        dt.size = sizeof(dt);

        if (p_GetTrophyInfo(g_ctx, g_hdl, id, &d, &dt) == 0) {
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

    memset(&d, 0, sizeof(d));
    memset(&dt, 0, sizeof(dt));
    d.size = sizeof(d);
    dt.size = sizeof(dt);

    if (p_GetTrophyInfo(g_ctx, g_hdl, id, &d, &dt) != 0)
        return 0;

    return dt.IsUnlocked ? 1 : 0;
}

int main(void)
{
    static int ids[MAX_ORDER];
    static int delays[MAX_ORDER];

    log_open();
    log_str("=== trophy unlocker (pkg) start ===\n");

    if (resolve_symbols() != 0)
        return 1;

    int uid = 0;
    if (sceUserServiceGetInitialUser(&uid) != 0) {
        sceUserServiceGetForegroundUser(&uid);
    }
    log_int("user id ", uid);

    int rc = p_CreateContext(&g_ctx, uid, 0, 0);
    if (rc != 0) {
        log_int("CreateContext failed 0x", rc);
        return 1;
    }

    rc = p_CreateHandle(&g_hdl);
    if (rc != 0) {
        log_int("CreateHandle failed 0x", rc);
        return 1;
    }

    rc = p_RegisterContext(g_ctx, g_hdl, (void *)on_registered, NULL, 0);
    if (rc != 0) {
        log_int("RegisterContext failed 0x", rc);
        return 1;
    }

    for (int i = 0; i < 100 && g_registered == 0; i++)
        sceKernelUsleep(100000);

    if (g_registered != 1) {
        log_str("context 没注册成功，放弃\n");
        return 1;
    }
    log_str("context registered\n");

    sceKernelUsleep(500000);

    int count = trophy_count();
    log_int("trophy count ", count);
    if (count <= 0) {
        log_str("这个包没有奖杯集\n");
        return 1;
    }

    int n = load_order(ids, delays, MAX_ORDER);
    int ok = 0, skipped = 0;

    if (n <= 0) {
        for (int id = 0; id < count; id++) {
            if (is_unlocked(id)) {
                skipped++;
                continue;
            }
            int plat = -1;
            if (p_UnlockTrophy(g_ctx, g_hdl, id, &plat) == 0)
                ok++;
        }
    } else {
        for (int i = 0; i < n; i++) {
            int id = ids[i];
            if (id >= count)
                continue;

            if (is_unlocked(id)) {
                skipped++;
                continue;
            }

            int plat = -1;
            if (p_UnlockTrophy(g_ctx, g_hdl, id, &plat) == 0)
                ok++;
            else
                log_int("unlock failed id ", id);

            sceKernelUsleep((unsigned int)delays[i] * 1000u);
        }
    }

    log_int("done: unlocked ", ok);
    log_int("done: already had ", skipped);
    log_str("=== end ===\n");

    sceKernelUsleep(1000000);
    return 0;
}
