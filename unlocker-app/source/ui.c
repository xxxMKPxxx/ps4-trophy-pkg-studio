/*
 * 界面：奖杯列表 + 手动解锁 + 一键解锁（带进度/倒计时）
 *
 * 操作：
 *   ↑/↓        移动光标（L2/R2 翻页）
 *   X          解锁光标处奖杯
 *   L1 + ○     一键解锁（按顺序表；运行中再按 = 停止）
 *   △          刷新解锁状态
 *   ○          不再退出程序（完成界面里 ○ 退回列表）
 *
 * 关闭应用请用 PS 键 →「关闭应用程序」：PS4 的 big app 自己退出会触发
 * 崩溃报告，所以这个界面是常驻的，按 ○ 不再结束进程。
 *
 * 计时一律用 sceKernelGetProcessTime()（微秒，单调），不按帧数：
 * 一帧实际要 60ms 以上（画 1920x1080 + 逐字渲染 + 等 flip），
 * 按「50 帧 = 1 秒」算会让表里的 1 秒变成实际 3 秒多。
 */
#include <stdarg.h>
#include <stdint.h>
#include <stdio.h>
#include <string.h>

#include <orbis/libkernel.h>
#include <orbis/Pad.h>

#include "gfx.h"
#include "font.h"
#include "ui.h"
#include "log.h"

#define BG          GFX_RGB(16, 18, 24)
#define PANEL       GFX_RGB(28, 32, 42)
#define PANEL2      GFX_RGB(44, 52, 68)
#define TEXT        GFX_RGB(232, 236, 244)
#define TEXT_DIM    GFX_RGB(140, 150, 168)
#define ACCENT      GFX_RGB(64, 156, 255)
#define GREEN       GFX_RGB(78, 201, 126)
#define YELLOW      GFX_RGB(240, 196, 92)

#define US_PER_SEC  1000000ULL
#define ROW_H       56
#define LIST_TOP    176
#define LIST_ROWS   14
#define LIST_X      96
#define LIST_W      (GFX_WIDTH - 96 * 2)

/* 关键文字全部放在左半边：部分电视/截图会裁掉右侧，贴右对齐的文字会看不见 */
#define SAFE_TEXT_W 1200
#define STATUS_X    (LIST_X + 1320)
#define BAR_W       1480

static int s_font_title = -1;
static int s_font_row = -1;
static int s_font_small = -1;

static int s_cur;
static int s_top;
static int s_mode;                /* 0 = 列表, 1 = 一键解锁中, 2 = 完成 */
static int s_plan_id[512];
static int s_plan_delay[512];
static int s_plan_len;
static int s_plan_pos;
static uint64_t s_wait_until;     /* 绝对时间，0 = 不用等 */
static int s_ok, s_skip, s_fail;
static uint64_t s_toast_until;
static uint64_t s_l1_until;       /* L1 松开后的组合键宽容窗口 */
static char s_toast[192];

/* ---------------- 小工具 ---------------- */

static uint64_t now_us(void)
{
    return sceKernelGetProcessTime();
}

static void toast(const char *fmt, ...)
{
    va_list ap;

    va_start(ap, fmt);
    vsnprintf(s_toast, sizeof(s_toast), fmt, ap);
    va_end(ap);

    s_toast_until = now_us() + 3ULL * US_PER_SEC;
}

static int clamp(int v, int lo, int hi)
{
    if (v < lo) return lo;
    if (v > hi) return hi;
    return v;
}

/* 所有文字统一走这里：限宽 + 超出补省略号 + 绝不画到屏幕外 */
static void text(int slot, int x, int y, int max_w, const char *s, uint32_t color)
{
    font_draw_fit(slot, x, y, max_w, s, color);
}

/* ---------------- 绘制 ---------------- */

static void draw_header(int total, int unlocked)
{
    char buf[128];

    gfx_fill(0, 0, GFX_WIDTH, 120, PANEL);
    gfx_fill(0, 118, GFX_WIDTH, 2, ACCENT);

    text(s_font_title, LIST_X, 26, 420, "奖杯解锁器", TEXT);

    snprintf(buf, sizeof(buf), "用户 %d", api_user_id());
    text(s_font_small, LIST_X + 320, 42, 300, buf, TEXT_DIM);

    /* 原来贴着右边缘画，右半边被裁时只剩「已解」两个字 */
    snprintf(buf, sizeof(buf), "已解锁 %d / %d", unlocked, total);
    text(s_font_small, LIST_X + 660, 42, 420, buf, GREEN);
}

static void draw_row(int y, int id, const char *name, int is_unlocked, int selected)
{
    char buf[32];

    gfx_fill(LIST_X, y, LIST_W, ROW_H - 6, selected ? PANEL2 : PANEL);
    if (selected)
        gfx_fill(LIST_X, y, 6, ROW_H - 6, ACCENT);

    snprintf(buf, sizeof(buf), "%03d", id);
    text(s_font_small, LIST_X + 26, y + 14, 90, buf, selected ? ACCENT : TEXT_DIM);

    text(s_font_row, LIST_X + 116, y + 6,
         STATUS_X - (LIST_X + 116) - 32, name, is_unlocked ? TEXT_DIM : TEXT);

    if (is_unlocked)
        text(s_font_small, STATUS_X, y + 14, 180, "已解锁", GREEN);
}

static void draw_footer(void)
{
    gfx_fill(0, GFX_HEIGHT - 96, GFX_WIDTH, 96, PANEL);
    text(s_font_small, LIST_X, GFX_HEIGHT - 74, SAFE_TEXT_W + 400,
         "↑↓ 选择   L2/R2 翻页   X 解锁   L1+○ 一键解锁   △ 刷新", TEXT_DIM);
    text(s_font_small, LIST_X, GFX_HEIGHT - 38, SAFE_TEXT_W + 400,
         "关闭应用：按 PS 键 → 关闭应用程序（○ 不再退出）", TEXT_DIM);
}

/* 取计划里某个位置的名字，取不到就给个占位 */
static void plan_name(int id, char *out, int cap, int *unlocked)
{
    if (id < 0 || api_get(id, out, cap, unlocked) != 0 || !out[0])
        snprintf(out, (size_t)cap, "（无名称）");
}

static void draw_auto_panel(void)
{
    int y = GFX_HEIGHT - 300;
    int bar_y = y + 166;
    int done = s_plan_pos > s_plan_len ? s_plan_len : s_plan_pos;
    uint64_t t = now_us();
    int cur_id = (s_plan_pos < s_plan_len) ? s_plan_id[s_plan_pos] : -1;
    char buf[320];
    char name[128];
    int u = 0;
    int pct = s_plan_len > 0 ? done * 100 / s_plan_len : 0;

    gfx_fill(0, y, GFX_WIDTH, 300, PANEL);
    gfx_fill(0, y, GFX_WIDTH, 2, ACCENT);

    /* 第一行：进度 + ID（用户要求一键解锁这里也显示奖杯 ID） */
    if (cur_id >= 0)
        snprintf(buf, sizeof(buf), "一键解锁   第 %d / %d 个    ID %03d",
                 s_plan_pos + 1, s_plan_len, cur_id);
    else
        snprintf(buf, sizeof(buf), "一键解锁   第 %d / %d 个", s_plan_pos + 1, s_plan_len);
    text(s_font_row, LIST_X, y + 20, SAFE_TEXT_W, buf, TEXT);

    /* 第二行：这个 ID 的名字 */
    if (cur_id >= 0) {
        plan_name(cur_id, name, sizeof(name), &u);
        snprintf(buf, sizeof(buf), "名称：%s%s", name, u ? "   （已解锁，将跳过）" : "");
    } else {
        snprintf(buf, sizeof(buf), "名称：—");
    }
    text(s_font_small, LIST_X, y + 70, SAFE_TEXT_W, buf, TEXT_DIM);

    /* 第三行：真实秒数倒计时 + 下一个 ID */
    if (s_wait_until > t) {
        uint64_t rem = s_wait_until - t;
        int sec = (int)((rem + US_PER_SEC - 1) / US_PER_SEC);
        snprintf(buf, sizeof(buf), "等待 %d 秒    下一个 ID %03d", sec,
                 cur_id >= 0 ? cur_id : 0);
        text(s_font_small, LIST_X, y + 112, SAFE_TEXT_W, buf, YELLOW);
    } else {
        text(s_font_small, LIST_X, y + 112, SAFE_TEXT_W, "正在解锁…", YELLOW);
    }

    gfx_fill(LIST_X, bar_y, BAR_W, 18, PANEL2);
    if (s_plan_len > 0)
        gfx_fill(LIST_X, bar_y, BAR_W * done / s_plan_len, 18, ACCENT);

    snprintf(buf, sizeof(buf), "%d%%", pct);
    text(s_font_small, LIST_X + BAR_W + 24, bar_y - 6, 140, buf, TEXT_DIM);

    snprintf(buf, sizeof(buf), "成功 %d   跳过 %d   失败 %d      （L1+○ 停止）",
             s_ok, s_skip, s_fail);
    text(s_font_small, LIST_X, y + 226, SAFE_TEXT_W, buf, TEXT_DIM);
}

static void draw_done_panel(void)
{
    int y = GFX_HEIGHT - 300;
    char buf[256];

    gfx_fill(0, y, GFX_WIDTH, 300, PANEL);
    gfx_fill(0, y, GFX_WIDTH, 2, GREEN);

    text(s_font_row, LIST_X, y + 40, SAFE_TEXT_W, "完成", GREEN);

    snprintf(buf, sizeof(buf), "成功解锁 %d 个，跳过已解锁 %d 个，失败 %d 个",
             s_ok, s_skip, s_fail);
    text(s_font_small, LIST_X, y + 110, SAFE_TEXT_W, buf, TEXT);

    text(s_font_small, LIST_X, y + 180, SAFE_TEXT_W, "X 返回列表    关闭应用请按 PS 键", TEXT_DIM);
}

static void draw_toast(void)
{
    int w;
    uint64_t t = now_us();

    if (t >= s_toast_until || !s_toast[0])
        return;

    /* 左对齐固定位置：居中框在右侧被裁时看不见 */
    w = font_text_width(s_font_small, s_toast) + 64;
    if (w > SAFE_TEXT_W)
        w = SAFE_TEXT_W;

    gfx_fill(LIST_X, 132, w, 56, PANEL2);
    gfx_frame_rect(LIST_X, 132, w, 56, 2, ACCENT);
    text(s_font_small, LIST_X + 32, 146, w - 48, s_toast, TEXT);
}

/* ---------------- 一键解锁 ---------------- */

static void build_plan(void)
{
    int n = api_order_len();
    int total = api_count();
    int j = 0;

    s_plan_len = 0;
    s_plan_pos = 0;
    s_wait_until = 0;

    if (n > 0) {
        for (int i = 0; i < n && s_plan_len < 512; i++) {
            int id = 0, delay = 0;
            if (api_order_get(i, &id, &delay) != 0)
                continue;
            s_plan_id[s_plan_len] = id;
            s_plan_delay[s_plan_len] = delay;
            s_plan_len++;
        }

        /* 0 号是白金：不管表里怎么写，挪到最后 */
        for (int i = 0; i < s_plan_len; i++) {
            if (s_plan_id[i] == 0) {
                int d = s_plan_delay[i];
                for (int k = i; k < s_plan_len - 1; k++) {
                    s_plan_id[k] = s_plan_id[k + 1];
                    s_plan_delay[k] = s_plan_delay[k + 1];
                }
                s_plan_id[s_plan_len - 1] = 0;
                s_plan_delay[s_plan_len - 1] = d;
                break;
            }
        }
    } else {
        for (int id = 1; id < total && j < 500; id++) {
            s_plan_id[j] = id;
            s_plan_delay[j] = 1;
            j++;
        }
        s_plan_id[j] = 0;              /* 白金最后 */
        s_plan_delay[j] = 1;
        j++;
        s_plan_len = j;
    }
}

static void auto_tick(void)
{
    if (s_wait_until > now_us())
        return;                       /* 还在等这一个的延迟，界面照常绘制倒计时 */

    s_wait_until = 0;

    while (s_plan_pos < s_plan_len) {
        int id = s_plan_id[s_plan_pos];
        char name[128];
        int unlocked = 0;
        int rc;

        if (id >= api_count() || api_get(id, name, sizeof(name), &unlocked) != 0) {
            s_plan_pos++;
            continue;
        }

        if (unlocked) {
            s_skip++;
            s_plan_pos++;
            continue;
        }

        s_cur = id;
        rc = api_unlock(id);
        if (rc == 0) {
            s_ok++;
            toast("已解锁 ID %03d：%s", id, name);
            log_line("auto unlock ok id=%d", id);
        } else {
            s_fail++;
            toast("解锁失败 ID %03d：%s（0x%08x）", id, name, (uint32_t)rc);
            log_line("auto unlock fail id=%d rc=0x%08x", id, (uint32_t)rc);
        }

        s_plan_pos++;

        /* 从解锁动作结束的这一刻开始计，间隔就是表里写的秒数 */
        if (s_plan_pos < s_plan_len) {
            int d = s_plan_delay[s_plan_pos - 1];
            if (d < 0) d = 0;
            if (d > 600) d = 600;
            if (d > 0)
                s_wait_until = now_us() + (uint64_t)d * US_PER_SEC;
        }
        return;
    }

    s_mode = 2;
    log_line("auto done ok=%d skip=%d fail=%d", s_ok, s_skip, s_fail);
}

/* ---------------- 主循环 ---------------- */

void ui_run(void)
{
    int pad;
    uint32_t prev = 0;
    int total = api_count();

    if (scePadInit() != 0)
        return;

    pad = scePadOpen(api_user_id(), ORBIS_PAD_PORT_TYPE_STANDARD, 0, NULL);
    if (pad < 0) {
        log_line("scePadOpen failed");
        return;
    }

    s_font_title = font_size(40);
    s_font_row = font_size(28);
    s_font_small = font_size(22);

    if (s_font_title < 0 || s_font_row < 0 || s_font_small < 0) {
        log_line("font_size failed");
        return;
    }

    api_refresh();

    for (;;) {
        OrbisPadData pd;
        uint32_t now = 0, pressed;
        uint64_t t = now_us();

        memset(&pd, 0, sizeof(pd));
        if (scePadReadState(pad, &pd) == 0)
            now = pd.buttons;

        pressed = now & ~prev;
        prev = now;

        /* L1 和 ○ 很少在同一帧被采到：L1 松开后保留 1 秒（真实时间）窗口 */
        if (now & ORBIS_PAD_BUTTON_L1)
            s_l1_until = t + US_PER_SEC;

        /* ○ 不再退出程序：单独按只给提示（完成界面里仍可回列表），
           L1 + ○ 才是一键解锁 / 停止。关闭应用走 PS 键菜单。 */
        if (pressed & ORBIS_PAD_BUTTON_CIRCLE) {
            int combo = (now & ORBIS_PAD_BUTTON_L1) || t < s_l1_until;
            log_line("circle pressed combo=%d mode=%d", combo, s_mode);
            s_l1_until = 0;
            if (combo) {
                if (s_mode == 0) {
                    s_ok = s_skip = s_fail = 0;
                    build_plan();
                    s_mode = 1;
                    log_line("auto start plan=%d", s_plan_len);
                } else if (s_mode == 1) {
                    s_mode = 2;
                    log_line("auto stopped pos=%d ok=%d", s_plan_pos, s_ok);
                } else {
                    s_mode = 0;
                }
            } else if (s_mode == 2) {
                s_mode = 0;
            } else {
                toast("关闭应用请按 PS 键 → 关闭应用程序");
                log_line("circle ignored (exit disabled)");
            }
        }

        if (s_mode == 0) {
            if (pressed & ORBIS_PAD_BUTTON_UP) {
                if (s_cur > 0) s_cur--;
            }
            if (pressed & ORBIS_PAD_BUTTON_DOWN) {
                if (s_cur < total - 1) s_cur++;
            }
            /* 翻页用 L2/R2 —— L1 是一键解锁的组合键，不能兼任翻页 */
            if (pressed & ORBIS_PAD_BUTTON_L2)
                s_cur = clamp(s_cur - LIST_ROWS, 0, total - 1);
            if (pressed & ORBIS_PAD_BUTTON_R2)
                s_cur = clamp(s_cur + LIST_ROWS, 0, total - 1);

            if (pressed & ORBIS_PAD_BUTTON_CROSS) {
                char name[128];
                int unlocked = 0;

                if (api_get(s_cur, name, sizeof(name), &unlocked) == 0) {
                    if (s_cur == 0 && !unlocked) {
                        /* 0 号是白金，系统不允许单独解（0x8055160d） */
                        toast("0 号是白金，请用 L1+○ 一键解锁（自动放最后）");
                    } else if (unlocked) {
                        toast("这个已经解锁了：%s", name);
                    } else {
                        int rc = api_unlock(s_cur);
                        if (rc == 0) {
                            toast("已解锁 ID %03d：%s", s_cur, name);
                            log_line("manual unlock ok id=%d", s_cur);
                        } else {
                            toast("解锁失败 ID %03d：%s（0x%08x）", s_cur, name, (uint32_t)rc);
                            log_line("manual unlock fail id=%d rc=0x%08x", s_cur, (uint32_t)rc);
                        }
                    }
                }
            }

            if (pressed & ORBIS_PAD_BUTTON_TRIANGLE) {
                api_refresh();
                toast("已刷新");
            }

        } else if (s_mode == 1) {
            /* 停止走上面的 L1 + ○ */
        } else {
            if (pressed & ORBIS_PAD_BUTTON_CROSS)
                s_mode = 0;
        }

        if (s_mode == 1)
            auto_tick();

        if (s_cur < s_top)
            s_top = s_cur;
        if (s_cur >= s_top + LIST_ROWS)
            s_top = s_cur - LIST_ROWS + 1;

        gfx_clear(BG);

        {
            int unlocked_count = 0;

            for (int i = 0; i < total; i++) {
                char nm[128];
                int u = 0;
                if (api_get(i, nm, sizeof(nm), &u) == 0 && u)
                    unlocked_count++;
            }
            draw_header(total, unlocked_count);
        }

        for (int r = 0; r < LIST_ROWS; r++) {
            int id = s_top + r;
            char nm[128];
            int u = 0;

            if (id >= total)
                break;

            if (api_get(id, nm, sizeof(nm), &u) != 0)
                continue;

            draw_row(LIST_TOP + r * ROW_H, id, nm, u, id == s_cur);
        }

        if (s_mode == 1)
            draw_auto_panel();
        else if (s_mode == 2)
            draw_done_panel();
        else
            draw_footer();

        draw_toast();
        gfx_present();

        sceKernelUsleep(12000);
    }

    scePadClose(pad);
}
