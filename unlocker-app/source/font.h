/*
 * FreeType 文字绘制（用系统的 libSceFreeTypeOl，和 OpenOrbis samples/font 同一套）
 *
 * 字体文件由 pkg 自带：/app0/font.ttf（PkgStudio 打 pkg 时会塞进去）。
 */
#ifndef FONT_H
#define FONT_H

#include <stdint.h>

#define FONT_MAX_SLOTS 8

int  font_init(const char *path);            /* 初始化 FreeType + 载入字体，0 = 成功 */
int  font_size(int px);                      /* 用指定像素高度开一个 slot，返回 slot 号，<0 = 失败 */
int  font_line_height(int slot);
int  font_text_width(int slot, const char *utf8);
int  font_fit_chars(int slot, const char *utf8, int max_px);   /* 能放进 max_px 的字符数（UTF-8 字符） */

void font_draw(int slot, int x, int y_top, const char *utf8, uint32_t color);
/* y_top 是文字框顶部，内部按 ascent 对齐 */

/* 按像素宽度裁剪绘制：逐字符累加 advance，放不下就停并补 "..."。
   用这个代替「先算字符数、再按字节复制」——中文是 3 字节，
   老做法会把名字截成原来的 1/3，而且也容易画出屏幕右边。 */
void font_draw_fit(int slot, int x, int y_top, int max_px, const char *utf8, uint32_t color);

#endif
