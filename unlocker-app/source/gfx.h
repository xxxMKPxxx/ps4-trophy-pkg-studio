/*
 * 极简 framebuffer 绘制（sceVideoOut + 直接内存）
 *
 * 1920x1080 / 32bit，双缓冲。像素格式和 OpenOrbis 的 samples/_common/graphics.cpp 一致：
 *   0x80000000 | (r << 16) | (g << 8) | b
 */
#ifndef GFX_H
#define GFX_H

#include <stdint.h>

#define GFX_W 1920
#define GFX_H 1080

#define GFX_RGB(r, g, b) \
    (0x80000000u | ((uint32_t)(r) << 16) | ((uint32_t)(g) << 8) | (uint32_t)(b))

int  gfx_init(void);
int  gfx_ready(void);

void gfx_clear(uint32_t color);
void gfx_fill(int x, int y, int w, int h, uint32_t color);
void gfx_frame_rect(int x, int y, int w, int h, int t, uint32_t color);

/* 8bit 灰度位图 + alpha 混合（给 font.c 用） */
void gfx_blit_alpha(int x, int y, const uint8_t *src, int w, int h, int pitch, uint32_t color);

/* 画完调用：提交并等这一帧真的显示出来（双缓冲要等，否则下一帧会写正在显示的缓冲） */
void gfx_present(void);

#endif
