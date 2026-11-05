/*
 * Trophy Unlocker（独立 pkg 版，带界面）
 *
 * 启动就在系统奖杯管理器里调 sceNpTrophyCreateContext / UnlockTrophy
 * （真解锁，不是 Apollo 那种只改本地显示的 fake unlock）。
 *
 * 界面：手选奖杯按 X 解锁，OPTIONS 一键解锁（按顺序表，带进度和倒计时）。
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

static void hold_and_exit(void)
{
    if (g_logfd >= 0)
        sceKernelFsync(g_logfd);
    sceKernelSleep(FAIL_HOLD_SEC);
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

int main(void)
{
    int rc;
    int uid = 0;

    log_open();
    log_str("=== trophy unlocker (pkg) start ===\n");
    if (g_logfd < 0) {
        hold_and_exit();
        return 1;
    }

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
        hold_and_exit();
        return 1;
    }
    g_uid = uid;

    /* 2. 系统奖杯模块 */
    rc = sceSysmoduleLoadModule(ORBIS_SYSMODULE_NP_TROPHY);
    log_hex("LoadModule NP_TROPHY ", (uint32_t)rc);

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

    rc = sceNpTrophyRegisterContext(g_ctx, g_hdl, 0);
    log_hex("RegisterContext ", (uint32_t)rc);
    if (rc != 0) {
        hold_and_exit();
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
        hold_and_exit();
        return 1;
    }

    api_refresh();
    load_order();

    /* 4. 画面 + 字体，失败就只能写日志了 */
    rc = gfx_init();
    log_hex("gfx_init ", (uint32_t)rc);
    if (rc != 0) {
        hold_and_exit();
        return 1;
    }

    rc = font_init(FONT_PATH);
    log_hex("font_init ", (uint32_t)rc);
    if (rc != 0) {
        log_str("字体加载失败（/app0/font.ttf）\n");
        hold_and_exit();
        return 1;
    }

    log_str("entering ui\n");
    ui_run();

    log_str("=== end ===\n");
    sceNpTrophyDestroyHandle(g_hdl);
    sceNpTrophyDestroyContext(g_ctx);
    if (g_logfd >= 0)
        sceKernelFsync(g_logfd);

    return 0;
}
