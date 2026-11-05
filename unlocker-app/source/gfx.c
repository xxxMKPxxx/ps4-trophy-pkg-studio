#include <string.h>

#include <orbis/libkernel.h>
#include <orbis/VideoOut.h>

#include "gfx.h"
#include "log.h"

static int      s_video = -1;
static int      s_ready;
static void    *s_videoMem;
static off_t    s_directOff;
static size_t   s_directSize;
static int      s_cur;
static int      s_frame;
static uint32_t *s_fb[2];
static OrbisKernelEqueue       s_flipQueue;
static OrbisVideoOutBufferAttribute s_attr;

int gfx_ready(void)
{
    return s_ready;
}

int gfx_init(void)
{
    const size_t stride = (size_t)GFX_WIDTH * 4;
    const size_t one = stride * GFX_HEIGHT;
    const size_t align = 0x200000;

    s_video = sceVideoOutOpen(ORBIS_VIDEO_USER_MAIN, ORBIS_VIDEO_OUT_BUS_MAIN, 0, NULL);
    if (s_video < 0)
        return -1;

    if (sceKernelCreateEqueue(&s_flipQueue, "unlocker flip queue") < 0)
        return -2;
    {
        int32_t r = sceVideoOutAddFlipEvent(s_flipQueue, s_video, NULL);
        log_line("gfx: AddFlipEvent rc=0x%08x", (uint32_t)r);
    }

    s_directSize = (one * 2 + align - 1) / align * align;
    if (sceKernelAllocateDirectMemory(0, sceKernelGetDirectMemorySize(),
                                      s_directSize, align, 3, &s_directOff) < 0)
        return -3;

    if (sceKernelMapDirectMemory(&s_videoMem, s_directSize, 0x33, 0, s_directOff, align) < 0)
        return -4;

    s_fb[0] = (uint32_t *)s_videoMem;
    s_fb[1] = (uint32_t *)((uint8_t *)s_videoMem + one);

    sceVideoOutSetBufferAttribute(&s_attr, 0x80000000, 1, 0, GFX_WIDTH, GFX_HEIGHT, GFX_WIDTH);

    {
        void *buffers[2];
        int32_t r;
        buffers[0] = s_fb[0];
        buffers[1] = s_fb[1];
        r = sceVideoOutRegisterBuffers(s_video, 0, buffers, 2, &s_attr);
        log_line("gfx: RegisterBuffers rc=0x%08x", (uint32_t)r);
        if (r != 0)
            return -5;
    }

    sceVideoOutSetFlipRate(s_video, 0);

    s_cur = 0;
    s_frame = 0;
    s_ready = 1;
    return 0;
}

void gfx_clear(uint32_t color)
{
    if (!s_ready)
        return;

    uint32_t *p = s_fb[s_cur];
    size_t n = (size_t)GFX_WIDTH * GFX_HEIGHT;
    for (size_t i = 0; i < n; i++)
        p[i] = color;
}

void gfx_fill(int x, int y, int w, int h, uint32_t color)
{
    if (!s_ready)
        return;

    int x0 = x < 0 ? 0 : x;
    int y0 = y < 0 ? 0 : y;
    int x1 = x + w > GFX_WIDTH ? GFX_WIDTH : x + w;
    int y1 = y + h > GFX_HEIGHT ? GFX_HEIGHT : y + h;

    if (x1 <= x0 || y1 <= y0)
        return;

    for (int yy = y0; yy < y1; yy++) {
        uint32_t *row = s_fb[s_cur] + (size_t)yy * GFX_WIDTH;
        for (int xx = x0; xx < x1; xx++)
            row[xx] = color;
    }
}

void gfx_frame_rect(int x, int y, int w, int h, int t, uint32_t color)
{
    gfx_fill(x, y, w, t, color);
    gfx_fill(x, y + h - t, w, t, color);
    gfx_fill(x, y, t, h, color);
    gfx_fill(x + w - t, y, t, h, color);
}

/* 给 font.c 用：把一块 8bit 灰度贴到当前缓冲（带简单 alpha 混合） */
void gfx_blit_alpha(int x, int y, const uint8_t *src, int w, int h, int pitch, uint32_t color)
{
    if (!s_ready)
        return;

    int r = (int)((color >> 16) & 0xFF);
    int g = (int)((color >> 8) & 0xFF);
    int b = (int)(color & 0xFF);

    for (int yy = 0; yy < h; yy++) {
        int dy = y + yy;
        if (dy < 0 || dy >= GFX_HEIGHT)
            continue;

        const uint8_t *s = src + (size_t)yy * pitch;
        uint32_t *row = s_fb[s_cur] + (size_t)dy * GFX_WIDTH;

        for (int xx = 0; xx < w; xx++) {
            int dx = x + xx;
            if (dx < 0 || dx >= GFX_WIDTH)
                continue;

            int a = s[xx];
            if (a == 0)
                continue;

            if (a == 255) {
                row[dx] = 0x80000000u | ((uint32_t)r << 16) | ((uint32_t)g << 8) | (uint32_t)b;
                continue;
            }

            uint32_t old = row[dx];
            int orr = (int)((old >> 16) & 0xFF);
            int ogg = (int)((old >> 8) & 0xFF);
            int obb = (int)(old & 0xFF);

            int nr = (r * a + orr * (255 - a)) / 255;
            int ng = (g * a + ogg * (255 - a)) / 255;
            int nb = (b * a + obb * (255 - a)) / 255;

            row[dx] = 0x80000000u | ((uint32_t)nr << 16) | ((uint32_t)ng << 8) | (uint32_t)nb;
        }
    }
}

void gfx_present(void)
{
    int rc;
    static int logged;

    if (!s_ready)
        return;

    s_frame++;
    rc = sceVideoOutSubmitFlip(s_video, s_cur, ORBIS_VIDEO_OUT_FLIP_VSYNC, s_frame);
    if (!logged) {
        log_line("gfx: first submitFlip rc=0x%08x frame=%d", (uint32_t)rc, s_frame);
        logged = 1;
    }

    /* 不依赖 flip event 队列：equeue 没事件时原实现会一直等（或立刻返回），
       首帧出不来 -> 系统启动画面一直盖着 -> 启动超时 CE-34878-0。
       改成轮询 flip status + 兜底等待，保证每帧都能推进。 */
    for (int i = 0; i < 30; i++) {
        OrbisVideoOutFlipStatus st;

        memset(&st, 0, sizeof(st));
        if (sceVideoOutGetFlipStatus(s_video, &st) == 0 && st.flipArg >= (int64_t)s_frame)
            break;
        sceKernelUsleep(1000);
    }
    sceKernelUsleep(2000);

    s_cur ^= 1;
}

void gfx_shutdown(void)
{
    if (!s_ready)
        return;

    s_ready = 0;

    /* 每一步都写日志：万一清理本身崩了，最后一行能直接指出是哪一步 */
    log_str("gfx: delete equeue\n");
    sceKernelDeleteEqueue(s_flipQueue);

    log_str("gfx: close video out\n");
    sceVideoOutClose(s_video);
    s_video = -1;

    if (s_videoMem) {
        log_str("gfx: munmap\n");
        sceKernelMunmap(s_videoMem, s_directSize);
        log_str("gfx: release direct memory\n");
        sceKernelReleaseDirectMemory(s_directOff, s_directSize);
        s_videoMem = NULL;
    }

    s_fb[0] = NULL;
    s_fb[1] = NULL;
}
