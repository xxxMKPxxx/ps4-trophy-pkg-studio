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
#include <stdbool.h>

#include <orbis/libkernel.h>
#include <orbis/Pad.h>
#include <orbis/UserService.h>
#include <orbis/NpTrophy.h>

#include <Detour.h>

#include "plugin_common.h"
#include "trophy_plugin.h"

/* 这些按键/端口常量在 orbis/_types/pad.h 里是 enum，不是宏。
 * 所以不能用 #ifdef 探测（#ifdef 只看宏），这里直接采用 ORBIS_ 命名。 */
typedef OrbisPadData pad_data_t;

#define BTN_CROSS    ORBIS_PAD_BUTTON_CROSS
#define BTN_CIRCLE   ORBIS_PAD_BUTTON_CIRCLE
#define BTN_TRIANGLE ORBIS_PAD_BUTTON_TRIANGLE
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

/* ---------------- 手柄输入 ----------------
 * 在游戏进程里 scePadOpen 经常拿不到句柄（端口已被游戏自己占用），
 * 所以主路径改成 hook 游戏自己的读手柄函数，白拿它每帧读到的按键。
 * 两种常见 API 都挂：scePadReadState 和 scePadRead（有些游戏只用后者）。
 */
typedef int32_t (*fn_pad_read_state)(int32_t, OrbisPadData *);
typedef int32_t (*fn_pad_read)(int32_t, OrbisPadData *, int32_t);

static fn_pad_read_state p_PadReadState;
static fn_pad_read_state p_PadReadStateOrig;
static Detour            g_padDetour;
static int               g_padHookOk;

static fn_pad_read       p_PadRead;
static fn_pad_read       p_PadReadOrig;
static Detour            g_padReadDetour;
static int               g_padReadHookOk;

static volatile uint32_t g_btn;        /* hook 里抓到的最新按键位 */
static volatile int      g_btnAlive;   /* hook 是否真的被调用过 */

static int32_t PadReadStateHook(int32_t handle, OrbisPadData *data)
{
    int32_t rc = p_PadReadStateOrig(handle, data);
    if (rc == 0 && data) {
        g_btn = data->buttons;
        g_btnAlive = 1;
    }
    return rc;
}

static int32_t PadReadHook(int32_t handle, OrbisPadData *data, int32_t num)
{
    int32_t rc = p_PadReadOrig(handle, data, num);
    if (rc > 0 && data) {
        g_btn = data->buttons;
        g_btnAlive = 1;
    }
    return rc;
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

/* 把游戏自己的读手柄函数挂上，白拿它读到的按键 */
static void install_pad_hook(void)
{
    int res = 0;
    int lib = sceKernelLoadStartModule("libScePad.sprx", 0, NULL, 0, NULL, &res);
    if (lib < 0) {
        debug_printf("load libScePad failed: 0x%08X\n", lib);
        return;
    }

    if (sceKernelDlsym(lib, "scePadReadState", (void **)&p_PadReadState) == 0 && p_PadReadState) {
        Detour_Construct(&g_padDetour, DetourMode_x64);
        p_PadReadStateOrig = (fn_pad_read_state)Detour_DetourFunction(
            &g_padDetour, (uint64_t)p_PadReadState, (void *)PadReadStateHook);
        g_padHookOk = (p_PadReadStateOrig != NULL);
    } else {
        debug_printf("dlsym scePadReadState failed\n");
    }

    if (sceKernelDlsym(lib, "scePadRead", (void **)&p_PadRead) == 0 && p_PadRead) {
        Detour_Construct(&g_padReadDetour, DetourMode_x64);
        p_PadReadOrig = (fn_pad_read)Detour_DetourFunction(
            &g_padReadDetour, (uint64_t)p_PadRead, (void *)PadReadHook);
        g_padReadHookOk = (p_PadReadOrig != NULL);
    } else {
        debug_printf("dlsym scePadRead failed\n");
    }

    debug_printf("pad hooks: readState=%d read=%d\n", g_padHookOk, g_padReadHookOk);
}

/* 兜底：自己开一个句柄。不同固件/不同进程里 userId 的约定不一样，
 * 这里逐个试并把返回码打进 klog，方便定位。 */
static int pad_try_open(void)
{
    int ids[5];
    int n = 0;
    int uid = 0;

    if (sceUserServiceGetForegroundUser(&uid) == 0)
        ids[n++] = uid;
    ids[n++] = 0x10000000;
    ids[n++] = 1;
    ids[n++] = 0;

    scePadInit();

    for (int i = 0; i < n; i++) {
        int h = scePadOpen(ids[i], PAD_PORT_STANDARD, 0, NULL);
        debug_printf("scePadOpen(uid=0x%08X) -> 0x%08X\n", ids[i], h);
        if (h >= 0)
            return h;
    }
    return -1;
}

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

        if (p_GetTrophyInfo(g_ctx, g_hdl, id, &d, &dt) == 0) {
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

/* 统一取按键：优先用 hook 到的游戏按键，其次才用自己开的句柄 */
static int g_padHandle = -1;

static uint32_t read_buttons(void)
{
    if (g_btnAlive)
        return g_btn;

    if (g_padHandle >= 0) {
        pad_data_t pd;
        memset(&pd, 0, sizeof(pd));
        if (scePadReadState(g_padHandle, &pd) == 0) {
            g_btn = pd.buttons;     /* 缓存一下，下次 hook 没跟上也能用 */
            return pd.buttons;
        }
    }
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

static void browse(int count)
{
    uint32_t prev = 0;
    int idx = 0;
    int active = 1;

    show(idx, count, idx);

    while (g_run && active) {
        uint32_t btn = read_buttons();
        uint32_t edge = btn & ~prev;
        prev = btn;

        if (edge & BTN_DOWN) {
            idx = (idx + 1) % count;
            show(idx, count, idx);
        } else if (edge & BTN_UP) {
            idx = (idx - 1 + count) % count;
            show(idx, count, idx);
        } else if (edge & BTN_CROSS) {
            int rc = unlock_one(idx);
            show(idx, count, idx);
            if (rc != 0)
                Notify(TEX_ICON_SYSTEM, "unlock failed: 0x%08X", rc);
        } else if (edge & BTN_TRIANGLE) {
            int ok = 0;
            for (int i = 0; i < count; i++)
                if (unlock_one(i) == 0)
                    ok++;
            Notify(TEX_ICON_SYSTEM, "unlocked %d / %d trophies", ok, count);
            active = 0;
        } else if (edge & BTN_CIRCLE) {
            active = 0;
        }

        sceKernelUsleep(50000);
    }
}

/* ---------------- 主线程 ---------------- */

static void *worker(void *arg)
{
    (void)arg;

    if (resolve_symbols() < 0) {
        Notify(TEX_ICON_SYSTEM, "trophy plugin: libSceNpTrophy not found");
        return NULL;
    }
    install_capture_hook();

    /* 等游戏自己注册奖杯上下文 */
    for (int waited = 0; waited < PICKER_TIMEOUT_US && g_ctx < 0 && g_run; waited += 200000)
        sceKernelUsleep(200000);

    if (g_ctx < 0 && try_self_context() < 0) {
        Notify(TEX_ICON_SYSTEM, "trophy plugin: no trophy context");
        debug_printf("no trophy context available\n");
        return NULL;
    }

    debug_printf("ready: ctx=%d handle=%d\n", g_ctx, g_hdl);

    /* 输入：先挂游戏的读手柄函数（不需要句柄），再尝试自己开一个兜底 */
    install_pad_hook();

    /* 输入源出现得很晚：插件在进程启动时注入，游戏往往过了 logo、过场、
     * 标题画面才开始读手柄。之前只等 2 秒就报 no input source，太早了。
     * 这里改成最多等 5 分钟，句柄每 5 秒重试一次。 */
    for (int sec = 0; sec < 300 && g_run; sec++) {
        if (!g_btnAlive && g_padHandle < 0 && (sec % 5) == 0)
            g_padHandle = pad_try_open();

        if (g_btnAlive || g_padHandle >= 0)
            break;

        if (sec == 15)
            Notify(TEX_ICON_SYSTEM, "trophy plugin: waiting for input...");

        sceKernelUsleep(1000000);
    }

    if (!g_btnAlive && g_padHandle < 0) {
        Notify(TEX_ICON_SYSTEM, "trophy plugin: no input after 5 min");
        debug_printf("no input: readState=%d read=%d, padOpen failed\n",
                     g_padHookOk, g_padReadHookOk);
        return NULL;
    }
    debug_printf("input ready: hookAlive=%d padHandle=%d\n", g_btnAlive, g_padHandle);

    while (g_run) {
        if (picker_step()) {
            int count = trophy_count();
            if (count > 0)
                browse(count);
        }
        sceKernelUsleep(50000);
    }

    if (g_padHandle >= 0)
        scePadClose(g_padHandle);

    if (g_padHookOk) {
        Detour_RestoreFunction(&g_padDetour);
        Detour_Destroy(&g_padDetour);
    }
    if (g_padReadHookOk) {
        Detour_RestoreFunction(&g_padReadDetour);
        Detour_Destroy(&g_padReadDetour);
    }
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
