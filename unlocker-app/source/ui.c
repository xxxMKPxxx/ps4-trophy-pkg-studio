/*
 * 界面：奖杯列表 + 手动解锁 + 一键解锁（带进度/倒计时）
 *
 * 操作：
 *   ↑/↓        移动光标（L2/R2 翻页）
 *   X          解锁光标处奖杯
 *   L1 + ○     一键解锁（按顺序表；运行中再按 = 停止）
 *   △          刷新解锁状态
 *   ○          退出（完成界面里 ○ 退回列表）
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

#define TICKS_PER_SEC 50          /* 主循环 20ms 一跳 */
#define ROW_H         56
#define LIST_TOP      176
#define LIST_ROWS     14
#define LIST_X        96
#define LIST_W        (GFX_WIDTH - 96 * 2)

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
static int s_wait_ticks;
static int s_ok, s_skip, s_fail;
static int s_toast_ticks;
static char s_toast[192];

/* ---------------- 小工具 ---------------- */

static void toast(const char *fmt, ...)
{
    va_list ap;

    va_start(ap, fmt);
    vsnprintf(s_toast, sizeof(s_toast), fmt, ap);
    va_end(ap);

    s_toast_ticks = 3 * TICKS_PER_SEC;
}

static int clamp(int v, int lo, int hi)
{
    if (v < lo) return lo;
    if (v > hi) return hi;
    return v;
}

/* ---------------- 绘制 ---------------- */

static void text_fit(int slot, int x, int y, int max_w, const char *s, uint32_t color)
{
    char buf[512];
    int limit = font_fit_chars(slot, s, max_w);
    int i = 0;
    const char *p = s;

    while (*p && i < limit && i < (int)sizeof(buf) - 4) {
        unsigned char c = (unsigned char)*p;
        int len = 1;

        if (c >= 0xF0) len = 4;
        else if (c >= 0xE0) len = 3;
        else if (c >= 0xC0) len = 2;

        for (int k = 0; k < len && *p; k++)
            buf[i++] = *p++;
    }

    buf[i] = 0;
    font_draw(slot, x, y, buf, color);
}

static void draw_header(int total, int unlocked)
{
    char buf[128];

    gfx_fill(0, 0, GFX_WIDTH, 120, PANEL);
    gfx_fill(0, 118, GFX_WIDTH, 2, ACCENT);

    font_draw(s_font_title, LIST_X, 30, "奖杯解锁器", TEXT);

    snprintf(buf, sizeof(buf), "用户 %d", api_user_id());
    font_draw(s_font_small, LIST_X + 300, 46, buf, TEXT_DIM);

    snprintf(buf, sizeof(buf), "已解锁 %d / %d", unlocked, total);
    font_draw(s_font_small, GFX_WIDTH - LIST_X - font_text_width(s_font_small, buf), 46, buf, GREEN);
}

static void draw_row(int y, int id, const char *name, int is_unlocked, int selected)
{
    char buf[32];

    gfx_fill(LIST_X, y, LIST_W, ROW_H - 6, selected ? PANEL2 : PANEL);
    if (selected)
        gfx_fill(LIST_X, y, 6, ROW_H - 6, ACCENT);

    snprintf(buf, sizeof(buf), "%03d", id);
    font_draw(s_font_small, LIST_X + 26, y + 14, buf, selected ? ACCENT : TEXT_DIM);

    text_fit(s_font_row, LIST_X + 116, y + 6, LIST_W - 380, name, is_unlocked ? TEXT_DIM : TEXT);

    if (is_unlocked) {
        const char *s = "已解锁";
        font_draw(s_font_small, LIST_X + LIST_W - font_text_width(s_font_small, s) - 26, y + 14, s, GREEN);
    }
}

static void draw_footer(void)
{
    gfx_fill(0, GFX_HEIGHT - 96, GFX_WIDTH, 96, PANEL);
    font_draw(s_font_small, LIST_X, GFX_HEIGHT - 70,
              "↑↓ 选择   L2/R2 翻页   X 解锁   L1+○ 一键解锁   △ 刷新   ○ 退出", TEXT_DIM);
}

static void draw_auto_panel(void)
{
    int y = GFX_HEIGHT - 300;
    int bar_y = y + 150;
    int done = s_plan_pos > s_plan_len ? s_plan_len : s_plan_pos;
    char buf[320];
    char name[128];
    int name_id = (s_plan_pos < s_plan_len) ? s_plan_id[s_plan_pos] : -1;
    int u = 0;

    gfx_fill(0, y, GFX_WIDTH, 300, PANEL);
    gfx_fill(0, y, GFX_WIDTH, 2, ACCENT);

    if (name_id >= 0 && api_get(name_id, name, sizeof(name), &u) == 0)
        snprintf(buf, sizeof(buf), "正在解锁  第 %d / %d 个：%s", s_plan_pos + 1, s_plan_len, name);
    else
        snprintf(buf, sizeof(buf), "正在解锁  第 %d / %d 个", s_plan_pos + 1, s_plan_len);

    text_fit(s_font_row, LIST_X, y + 40, LIST_W - 120, buf, TEXT);

    if (s_wait_ticks > 0) {
        int sec = (s_wait_ticks + TICKS_PER_SEC - 1) / TICKS_PER_SEC;
        snprintf(buf, sizeof(buf), "下一个 %d 秒后解锁…", sec);
        font_draw(s_font_small, LIST_X, y + 100, buf, YELLOW);
    } else {
        font_draw(s_font_small, LIST_X, y + 100, "解锁中…", YELLOW);
    }

    gfx_fill(LIST_X, bar_y, LIST_W - 96, 18, PANEL2);
    if (s_plan_len > 0)
        gfx_fill(LIST_X, bar_y, (LIST_W - 96) * done / s_plan_len, 18, ACCENT);

    snprintf(buf, sizeof(buf), "%d%%", s_plan_len > 0 ? done * 100 / s_plan_len : 0);
    font_draw(s_font_small, LIST_X + LIST_W - 80, bar_y - 4, buf, TEXT_DIM);

    snprintf(buf, sizeof(buf), "成功 %d   跳过 %d   失败 %d      （L1+○ 停止）", s_ok, s_skip, s_fail);
    font_draw(s_font_small, LIST_X, y + 200, buf, TEXT_DIM);
}

static void draw_done_panel(void)
{
    int y = GFX_HEIGHT - 300;
    char buf[256];

    gfx_fill(0, y, GFX_WIDTH, 300, PANEL);
    gfx_fill(0, y, GFX_WIDTH, 2, GREEN);

    font_draw(s_font_row, LIST_X, y + 40, "完成", GREEN);

    snprintf(buf, sizeof(buf), "成功解锁 %d 个，跳过已解锁 %d 个，失败 %d 个", s_ok, s_skip, s_fail);
    font_draw(s_font_small, LIST_X, y + 110, buf, TEXT);

    font_draw(s_font_small, LIST_X, y + 180, "○ 退出    X 返回列表", TEXT_DIM);
}

static void draw_toast(void)
{
    int w, x, y;

    if (s_toast_ticks <= 0 || !s_toast[0])
        return;

    w = font_text_width(s_font_small, s_toast) + 64;
    x = (GFX_WIDTH - w) / 2;
    y = 140;

    gfx_fill(x, y, w, 56, PANEL2);
    gfx_frame_rect(x, y, w, 56, 2, ACCENT);
    font_draw(s_font_small, x + 32, y + 14, s_toast, TEXT);
}

/* ---------------- 一键解锁 ---------------- */

static void build_plan(void)
{
    int n = api_order_len();
    int total = api_count();
    int j = 0;

    s_plan_len = 0;
    s_plan_pos = 0;
    s_wait_ticks = 0;

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
    while (s_plan_pos < s_plan_len) {
        int id = s_plan_id[s_plan_pos];
        char name[128];
        int unlocked = 0;
        int rc;

        if (s_wait_ticks > 0) {
            s_wait_ticks--;
            return;
        }

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
            toast("已解锁：%s", name);
            log_line("auto unlock ok id=%d", id);
        } else {
            s_fail++;
            toast("解锁失败：%s（0x%08x）", name, (uint32_t)rc);
            log_line("auto unlock fail id=%d rc=0x%08x", id, (uint32_t)rc);
        }

        s_plan_pos++;

        if (s_plan_pos < s_plan_len) {
            int d = s_plan_delay[s_plan_pos - 1];
            if (d < 0) d = 0;
            s_wait_ticks = d * TICKS_PER_SEC;
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
    int l1_recent = 0;            /* L1 松开后的宽容窗口，避免两键不同帧被当成单独 ○ */
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

        memset(&pd, 0, sizeof(pd));
        if (scePadReadState(pad, &pd) == 0)
            now = pd.buttons;

        pressed = now & ~prev;
        prev = now;

        /* L1 和 ○ 很少在同一帧被采到：L1 松开后保留约 1 秒的窗口，
           窗口内按 ○ 一律算组合键，不会被误判成"退出" */
        if (now & ORBIS_PAD_BUTTON_L1)
            l1_recent = 30;
        else if (l1_recent > 0)
            l1_recent--;

        /* ○：普通按 = 退出（完成界面 = 回列表）；L1 + ○ = 一键解锁 / 停止 */
        if (pressed & ORBIS_PAD_BUTTON_CIRCLE) {
            int combo = (now & ORBIS_PAD_BUTTON_L1) || l1_recent > 0;
            log_line("circle pressed combo=%d l1_recent=%d mode=%d", combo, l1_recent, s_mode);
            l1_recent = 0;
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
                log_line("user exit");
                break;
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
                            toast("已解锁：%s", name);
                            log_line("manual unlock ok id=%d", s_cur);
                        } else {
                            toast("解锁失败：%s（0x%08x）", name, (uint32_t)rc);
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

        if (s_toast_ticks > 0)
            s_toast_ticks--;

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

        sceKernelUsleep(20000);
    }

    scePadClose(pad);
}
