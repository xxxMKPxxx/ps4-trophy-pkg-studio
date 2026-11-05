/*
 * trophy_plugin.c - 在"游戏进程内"解锁奖杯
 *
 * 这是和独立 App 的根本区别：插件被 GoldHEN 注入到正在运行的游戏里，
 * 进程的 NP Communication ID 天然就是当前游戏，所以 sceNpTrophyUnlockTrophy
 * 走的是系统真正的奖杯管理器 —— 会写 TROPUSR.DAT / 更新时间戳 / 计入完成率，
 * 也就是你说的"实际解锁"，不是改 trophy_local.db 的假解锁。
 *
 * 拿上下文的方式：不去自己造第二个 trophry context（可能和游戏冲突），
 * 而是 hook 游戏自己的 sceNpTrophyRegisterContext，把它的 (context, handle)
 * 偷下来复用。
 *
 * 操作（游戏内）：
 *   L3 + R3   呼出/收起奖杯浏览
 *   十字键上下 切换奖杯（横幅提示当前奖杯名与状态）
 *   X         解锁当前奖杯
 *   三角      解锁全部
 *   O         收起浏览
 */
#include <string.h>
#include <stdio.h>
#include <stdlib.h>
#include <stdbool.h>

#include <orbis/libkernel.h>
#include <orbis/Pad.h>
#include <orbis/UserService.h>
#include <orbis/NpTrophy.h>

#include <Detour.h>

#include "plugin_common.h"
#include "trophy_plugin.h"
#include "unlock_order.h"

/* 这些按键/端口常量在 orbis/_types/pad.h 里是 enum，不是宏。
 * 所以不能用 #ifdef 探测（#ifdef 只看宏），这里直接采用 ORBIS_ 命名。 */
typedef OrbisPadData pad_data_t;

#define BTN_CROSS    ORBIS_PAD_BUTTON_CROSS
#define BTN_CIRCLE   ORBIS_PAD_BUTTON_CIRCLE
#define BTN_TRIANGLE ORBIS_PAD_BUTTON_TRIANGLE
#define BTN_SQUARE   ORBIS_PAD_BUTTON_SQUARE
#define BTN_UP       ORBIS_PAD_BUTTON_UP
#define BTN_DOWN     ORBIS_PAD_BUTTON_DOWN
#define BTN_L3       ORBIS_PAD_BUTTON_L3
#define BTN_R3       ORBIS_PAD_BUTTON_R3

#define PAD_PORT_STANDARD ORBIS_PAD_PORT_TYPE_STANDARD

#define TROPHY_MAX_IDS  256
#define PICKER_TIMEOUT_US  (120 * 1000 * 1000)   /* 等游戏注册上下文，最多 120s */

/* ---- libSceNpTrophy 里 dlsym 出来的函数指针 ---- */
typedef int32_t (*fn_create_ctx)(int32_t *, int32_t, uint32_t, uint64_t);
typedef int32_t (*fn_create_hdl)(int32_t *);
typedef int32_t (*fn_reg_ctx)(int32_t, int32_t, uint64_t);
typedef int32_t (*fn_get_info)(int32_t, int32_t, int32_t, OrbisNpTrophyDetails *, OrbisNpTrophyData *);
typedef int32_t (*fn_unlock)(int32_t, int32_t, int32_t, int32_t *);

static fn_create_ctx p_CreateContext;
static fn_create_hdl p_CreateHandle;
static fn_reg_ctx  p_RegisterContext;
static fn_get_info p_GetTrophyInfo;
static fn_unlock   p_UnlockTrophy;

static fn_reg_ctx  p_RegisterContextOrig;   /* detour 的 trampoline */

static int32_t    g_ctx = -1;
static int32_t    g_hdl = -1;
static volatile int g_run;
static Detour     g_detour;

/* 当前游戏的 TITLE_ID（CUSAxxxxx），用来找它自己的解锁顺序文件 */
static char       g_tid[16];

/* ---------------- 手柄输入 ----------------
 * 第三条路，也是唯一零风险的：
 *   scePadGetHandle() 返回"游戏已经打开的那个句柄"，我们只是拿它读状态。
 * 不 detour（不改游戏代码，不会和游戏抢执行）、不 scePadInit、
 * 不 scePadOpen（不会和游戏抢端口）。
 */
static int g_padHandle = -1;
static volatile uint32_t g_btn;        /* 最近一次读到的按键位 */
static volatile int      g_btnReady;   /* 是否已经成功读到过 */

static int pad_acquire(void)
{
    if (g_padHandle >= 0)
        return g_padHandle;

    int ids[4];
    int n = 0;
    int uid = 0;

    if (sceUserServiceGetForegroundUser(&uid) == 0)
        ids[n++] = uid;
    ids[n++] = 0x10000000;
    ids[n++] = 1;
    ids[n++] = 0;

    for (int i = 0; i < n; i++) {
        int h = scePadGetHandle(ids[i], ORBIS_PAD_PORT_TYPE_STANDARD, 0);
        if (h >= 0) {
            g_padHandle = h;
            debug_printf("pad handle acquired: uid=0x%08X handle=0x%08X\n", ids[i], h);
            return h;
        }
    }
    return -1;
}

static void pad_poll(void)
{
    if (pad_acquire() < 0)
        return;

    OrbisPadData pd;
    memset(&pd, 0, sizeof(pd));
    if (scePadReadState(g_padHandle, &pd) == 0) {
        g_btn = pd.buttons;
        g_btnReady = 1;
    }
}

/* ---------------- 上下文捕获 ---------------- */

static int32_t RegisterContextHook(int32_t ctx, int32_t hdl, uint64_t unk)
{
    if (g_ctx < 0) {
        g_ctx = ctx;
        g_hdl = hdl;
        debug_printf("captured trophry context: ctx=%d handle=%d\n", ctx, hdl);
    }
    return p_RegisterContextOrig(ctx, hdl, unk);
}

static int resolve_symbols(void)
{
    int res = 0;
    int lib = sceKernelLoadStartModule("libSceNpTrophy.sprx", 0, NULL, 0, NULL, &res);
    if (lib < 0) {
        debug_printf("load libSceNpTrophy failed: 0x%08X\n", lib);
        return -1;
    }

    sceKernelDlsym(lib, "sceNpTrophyCreateContext",   (void **)&p_CreateContext);
    sceKernelDlsym(lib, "sceNpTrophyCreateHandle",    (void **)&p_CreateHandle);
    sceKernelDlsym(lib, "sceNpTrophyRegisterContext", (void **)&p_RegisterContext);
    sceKernelDlsym(lib, "sceNpTrophyGetTrophyInfo",   (void **)&p_GetTrophyInfo);
    sceKernelDlsym(lib, "sceNpTrophyUnlockTrophy",    (void **)&p_UnlockTrophy);

    if (!p_RegisterContext || !p_GetTrophyInfo || !p_UnlockTrophy) {
        debug_printf("dlsym incomplete\n");
        return -1;
    }
    return 0;
}

static void install_capture_hook(void)
{
    Detour_Construct(&g_detour, DetourMode_x64);
    p_RegisterContextOrig = (fn_reg_ctx)Detour_DetourFunction(
        &g_detour, (uint64_t)p_RegisterContext, (void *)RegisterContextHook);
}

/* 尝试一次取句柄并读一帧，用于确认输入是否就绪 */

/* 游戏不用奖杯、或者 hook 没命中时的兜底：自己建一个上下文 */
static int try_self_context(void)
{
    if (!p_CreateContext || !p_CreateHandle || !p_RegisterContext)
        return -1;

    int32_t ctx = 0, hdl = 0;
    if (p_CreateContext(&ctx, 0, 0, 0) < 0) {
        debug_printf("CreateContext failed\n");
        return -1;
    }
    if (p_CreateHandle(&hdl) < 0) {
        debug_printf("CreateHandle failed\n");
        return -1;
    }
    if (p_RegisterContext(ctx, hdl, 0) < 0) {
        debug_printf("RegisterContext failed\n");
        return -1;
    }

    g_ctx = ctx;
    g_hdl = hdl;
    return 0;
}

/* ---------------- 奖杯查询 ---------------- */

static int trophy_count(void)
{
    int count = 0, miss = 0;

    for (int id = 0; id < TROPHY_MAX_IDS && miss < 5; id++) {
        OrbisNpTrophyDetails d;
        OrbisNpTrophyData dt;
        memset(&d, 0, sizeof(d));
        memset(&dt, 0, sizeof(dt));
        d.size = sizeof(d);
        dt.size = sizeof(dt);

        int rc = p_GetTrophyInfo(g_ctx, g_hdl, id, &d, &dt);
        if (id == 0)
            debug_printf("GetTrophyInfo(0) -> 0x%08X\n", rc);
        if (rc == 0) {
            count = id + 1;
            miss = 0;
        } else {
            miss++;
        }
    }
    return count;
}

static int trophy_info(int id, char *name, int name_len, int *unlocked)
{
    OrbisNpTrophyDetails d;
    OrbisNpTrophyData dt;
    memset(&d, 0, sizeof(d));
    memset(&dt, 0, sizeof(dt));
    d.size = sizeof(d);
    dt.size = sizeof(dt);

    if (p_GetTrophyInfo(g_ctx, g_hdl, id, &d, &dt) != 0)
        return -1;

    if (name && name_len > 0) {
        strncpy(name, d.TrophyName, (size_t)name_len - 1);
        name[name_len - 1] = '\0';
    }
    if (unlocked)
        *unlocked = dt.IsUnlocked ? 1 : 0;
    return 0;
}

static int unlock_one(int id)
{
    int32_t plat = -1;
    int rc = p_UnlockTrophy(g_ctx, g_hdl, id, &plat);
    debug_printf("unlock id=%d -> 0x%08X (platinum=%d)\n", id, rc, plat);
    return rc;
}

/* ---------------- 交互 ---------------- */

static void show(int id, int count, int idx)
{
    char name[128] = "?";
    int unlocked = 0;

    if (trophy_info(id, name, sizeof(name), &unlocked) != 0) {
        Notify(TEX_ICON_SYSTEM, "[%d/%d] id=%d  no data", idx + 1, count, id);
        return;
    }
    Notify(TEX_ICON_SYSTEM, "[%d/%d] id=%d  %s  %s",
           idx + 1, count, id, name, unlocked ? "UNLOCKED" : "locked");
}

/* 每次取值前读一帧游戏自己的句柄 */
static uint32_t read_buttons(void)
{
    pad_poll();
    return g_btn;
}

/* 返回 1 表示要退出浏览模式 */
static int picker_step(void)
{
    static uint32_t prev;
    static int idx;
    static int count;

    uint32_t btn = read_buttons();
    uint32_t edge = btn & ~prev;
    prev = btn;

    if (edge & (BTN_L3 | BTN_R3)) {
        count = trophy_count();
        idx = 0;
        if (count == 0) {
            Notify(TEX_ICON_SYSTEM, "this game has no trophy set");
        } else {
            show(idx, count, idx);
        }
        return 1;    /* 交给主循环切换模式 */
    }
    return 0;
}

/* ---------------- 批量解锁（按顺序表） ---------------- */

static void sleep_interruptible(int ms)
{
    while (ms > 0 && g_run) {
        int step = ms > 100 ? 100 : ms;
        sceKernelUsleep(step * 1000);
        ms -= step;
    }
}

/* 有顺序文件就按它走：跳过已解锁的，第一个没解锁的立刻解，之后按文件里的延迟。
 * 中途退出游戏（g_run 变 0）就停；下次再点批量解锁，会跳过已解锁的从剩下的继续。 */
static void unlock_all(int count)
{
    static int ids[UO_MAX];
    static int delays[UO_MAX];

    int n = uo_load(g_tid, ids, delays, UO_MAX);
    int ok = 0, skipped = 0, bad = 0;

    if (n <= 0) {
        /* 没加入顺序 → 保持原来的逻辑：按奖杯 ID 顺序全解，没有延迟 */
        debug_printf("unlock all: 无顺序文件，按默认顺序 0..%d\n", count - 1);
        /* 0 号是白金，提前解锁会出错：先 1..count-1，最后才 0 */
        for (int pass = 0; pass < 2 && g_run; pass++) {
            int from = (pass == 0) ? 1 : 0;
            int to = (pass == 0) ? count : 1;
            for (int id = from; id < to && g_run; id++) {
                char nm[128] = "";
                int un = 0;
                if (trophy_info(id, nm, sizeof(nm), &un) == 0 && un) {
                    skipped++;
                    continue;
                }
                if (unlock_one(id) == 0)
                    ok++;
            }
        }
        debug_printf("unlock all(default): ok=%d skipped=%d total=%d run=%d\n",
                     ok, skipped, count, g_run);
        if (g_run)
            Notify(TEX_ICON_SYSTEM, "unlocked %d, already had %d / %d", ok, skipped, count);
        return;
    }

    Notify(TEX_ICON_SYSTEM, "order: %d trophies, start in 1s", n);
    sleep_interruptible(1000);

    for (int i = 0; i < n && g_run; i++) {
        int id = ids[i];
        if (id >= count) {
            bad++;
            continue;
        }

        char nm[128] = "";
        int un = 0;
        if (trophy_info(id, nm, sizeof(nm), &un) == 0 && un) {
            skipped++;
            continue;
        }

        if (unlock_one(id) == 0) {
            ok++;
            Notify(TEX_ICON_SYSTEM, "[%d/%d] id=%d %s", i + 1, n, id, nm);
        }

        sleep_interruptible(delays[i]);
    }

    debug_printf("unlock all(order): ok=%d skipped=%d bad=%d n=%d total=%d run=%d\n",
                 ok, skipped, bad, n, count, g_run);

    if (g_run)
        Notify(TEX_ICON_SYSTEM, "unlocked %d, already had %d / %d", ok, skipped, count);
    else
        debug_printf("interrupted; next run skips unlocked and continues\n");
}

static void browse(int count)
{
    uint32_t prev = 0;
    int idx = 0;
    int active = 1;
    int confirmAll = 0;

    show(idx, count, idx);
    Notify(TEX_ICON_SYSTEM, "X=unlock  Tri=all  Sq=order  O=exit");

    while (g_run && active) {
        uint32_t btn = read_buttons();
        uint32_t edge = btn & ~prev;
        prev = btn;

        if (edge & BTN_DOWN) {
            idx = (idx + 1) % count;
            confirmAll = 0;
            show(idx, count, idx);
        } else if (edge & BTN_UP) {
            idx = (idx - 1 + count) % count;
            confirmAll = 0;
            show(idx, count, idx);
        } else if (edge & BTN_CROSS) {
            confirmAll = 0;

            char nm[128] = "";
            int un = 0;
            if (trophy_info(idx, nm, sizeof(nm), &un) == 0 && un) {
                Notify(TEX_ICON_SYSTEM, "already unlocked: %s", nm);
            } else {
                int rc = unlock_one(idx);
                if (rc == 0)
                    Notify(TEX_ICON_SYSTEM, "unlocked: %s", nm);
                else
                    Notify(TEX_ICON_SYSTEM, "unlock failed: 0x%08X", rc);
                show(idx, count, idx);
            }
        } else if (edge & BTN_TRIANGLE) {
            if (!confirmAll) {
                /* 解锁全部不可逆（同步后无法撤销），要求连按两次 */
                confirmAll = 1;
                Notify(TEX_ICON_SYSTEM, "unlock ALL %d trophies? press Triangle again", count);
            } else {
                unlock_all(count);
                active = 0;
            }
        } else if (edge & BTN_SQUARE) {
            /* 生成这个游戏的解锁顺序文件，再用 FTP 在电脑上改 */
            confirmAll = 0;
            int rc = uo_write_template(g_tid, count, 1500);
            if (rc == 0)
                Notify(TEX_ICON_SYSTEM, "order created: trophy_order_%s.csv", g_tid);
            else if (rc == 1)
                Notify(TEX_ICON_SYSTEM, "order exists: trophy_order_%s.csv", g_tid);
            else
                Notify(TEX_ICON_SYSTEM, "cannot write order file");
        } else if (edge & BTN_CIRCLE) {
            active = 0;
        }

        sceKernelUsleep(50000);
    }
}

/* ---------------- 主线程 ---------------- */

/* 设置文件 /data/GoldHEN/trophy_unlocker.cfg
 *   enable=0        插件只加载、只写日志，什么都不做（用来判断卡死是不是插件引起的）
 *   selfcontext=1   没抓到游戏的奖杯上下文时，自己新建一个（有风险，默认关） */
#define CFG_PATH "/data/GoldHEN/trophy_unlocker.cfg"

static int g_enable  = 1;
static int g_selfctx = 0;

static void load_cfg(void)
{
    FILE *f = fopen(CFG_PATH, "r");
    if (!f) {
        FILE *w = fopen(CFG_PATH, "w");
        if (w) {
            fputs("# enable=0 -> 插件只写日志，不做任何操作（排查卡死用）\n"
                  "# selfcontext=1 -> 抓不到游戏上下文时自己新建（有风险）\n"
                  "enable=1\nselfcontext=0\n", w);
            fclose(w);
        }
        debug_printf("cfg created: enable=1 selfcontext=0\n");
        return;
    }

    char line[128];
    while (fgets(line, sizeof(line), f)) {
        if (line[0] == '#' || line[0] == '\n' || line[0] == '\r')
            continue;
        if (!strncmp(line, "enable=", 7))
            g_enable = atoi(line + 7);
        else if (!strncmp(line, "selfcontext=", 12))
            g_selfctx = atoi(line + 12);
    }
    fclose(f);
    debug_printf("cfg: enable=%d selfcontext=%d\n", g_enable, g_selfctx);
}

static void *worker(void *arg)
{
    (void)arg;

    load_cfg();
    if (!g_enable) {
        debug_printf("disabled by cfg, worker exits\n");
        return NULL;
    }

    if (resolve_symbols() < 0) {
        Notify(TEX_ICON_SYSTEM, "trophy plugin: libSceNpTrophy not found");
        return NULL;
    }
    install_capture_hook();

    /* 等游戏自己注册奖杯上下文 */
    for (int waited = 0; waited < PICKER_TIMEOUT_US && g_ctx < 0 && g_run; waited += 200000)
        sceKernelUsleep(200000);

    if (g_ctx < 0 && (!g_selfctx || try_self_context() < 0)) {
        Notify(TEX_ICON_SYSTEM, "trophy plugin: no trophy context");
        debug_printf("no trophy context available (selfcontext=%d)\n", g_selfctx);
        return NULL;
    }

    debug_printf("ready: ctx=%d handle=%d\n", g_ctx, g_hdl);

    /* 从游戏自己的 param.sfo 取 TITLE_ID，用来找 /data/GoldHEN/trophy_unlocker/<TID>.csv */
    if (uo_title_id(g_tid, (int)sizeof(g_tid)) != 0)
        debug_printf("拿不到 TITLE_ID，顺序文件将不可用，只能按默认顺序解锁\n");

    /* 等系统把奖杯集挂好再数，太早会得到 0 */
    sceKernelUsleep(1000000);
    int n = trophy_count();
    debug_printf("trophy count=%d\n", n);
    Notify(TEX_ICON_SYSTEM, "Trophy Unlocker ready: %d trophies, L3/R3", n);

    /* 输入：拿游戏已打开的句柄。游戏通常过了 logo / 过场 / 标题画面
     * 才开始读手柄，所以这里最多等 5 分钟；等不到也继续待着，
     * 绝不退出、也绝不影响游戏本身。 */
    for (int sec = 0; sec < 300 && g_run && !g_btnReady; sec++) {
        pad_poll();
        if (sec == 15)
            Notify(TEX_ICON_SYSTEM, "trophy plugin: waiting for input...");
        sceKernelUsleep(1000000);
    }

    if (!g_btnReady)
        debug_printf("no input: scePadGetHandle failed (handle=%d)\n", g_padHandle);
    else
        debug_printf("input ready: handle=0x%08X\n", g_padHandle);

    while (g_run) {
        if (picker_step()) {
            int count = trophy_count();
            if (count > 0)
                browse(count);
        }
        sceKernelUsleep(50000);
    }

    /* 句柄是游戏打开的，不能由我们关闭 —— 关掉会把游戏的输入一起弄坏 */
    g_padHandle = -1;
    Detour_RestoreFunction(&g_detour);
    Detour_Destroy(&g_detour);
    return NULL;
}

int trophy_plugin_start(void)
{
    g_run = 1;

    /* 用 scePthreadCreate：PS4 的 pthread 实现在 libkernel 里，
     * 是这套生态（apollo、GoldHEN 各插件）建后台线程的标准做法。
     * sceKernelCreateThread 不在 libkernel 的 stub 里，链接会失败。 */
    OrbisPthread tid;
    int rc = scePthreadCreate(&tid, NULL, worker, NULL, "trophy_worker");
    if (rc != 0) {
        debug_printf("scePthreadCreate failed: 0x%08X\n", rc);
        return -1;
    }
    return 0;
}

void trophy_plugin_stop(void)
{
    g_run = 0;
}
