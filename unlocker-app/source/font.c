#include <string.h>
#include <stdint.h>

#include <orbis/Sysmodule.h>
#include <orbis/libkernel.h>

#include <proto-include.h>

#include "font.h"
#include "gfx.h"

static FT_Library s_lib;
static const char *s_path;
static FT_Face    s_face[FONT_MAX_SLOTS];
static int        s_slots;

static uint32_t utf8_next(const char **pp)
{
    const unsigned char *p = (const unsigned char *)*pp;
    uint32_t c = *p;
    int extra;

    if (c < 0x80) {
        *pp += 1;
        return c;
    } else if ((c & 0xE0) == 0xC0) {
        c &= 0x1F;
        extra = 1;
    } else if ((c & 0xF0) == 0xE0) {
        c &= 0x0F;
        extra = 2;
    } else if ((c & 0xF8) == 0xF0) {
        c &= 0x07;
        extra = 3;
    } else {
        *pp += 1;
        return '?';
    }

    p++;
    *pp += 1;

    for (int i = 0; i < extra; i++) {
        if ((*p & 0xC0) != 0x80)
            break;
        c = (c << 6) | (uint32_t)(*p & 0x3F);
        p++;
        *pp += 1;
    }

    return c;
}

int font_init(const char *path)
{
    int rc = sceSysmoduleLoadModule(ORBIS_SYSMODULE_FREETYPE_OL);
    if (rc < 0)
        return -1;

    if (FT_Init_FreeType(&s_lib) != 0)
        return -2;

    s_path = path;
    s_slots = 0;
    return 0;
}

int font_size(int px)
{
    FT_Face face;

    if (s_slots >= FONT_MAX_SLOTS || !s_path)
        return -1;

    if (FT_New_Face(s_lib, s_path, 0, &face) != 0)
        return -2;

    if (FT_Set_Pixel_Sizes(face, 0, (FT_UInt)px) != 0)
        return -3;

    s_face[s_slots] = face;
    return s_slots++;
}

static int face_ascender(FT_Face f)
{
    if (f && f->size)
        return (int)(f->size->metrics.ascender >> 6);
    return 0;
}

int font_line_height(int slot)
{
    FT_Face f;

    if (slot < 0 || slot >= FONT_MAX_SLOTS)
        return 0;

    f = s_face[slot];
    if (f && f->size)
        return (int)(f->size->metrics.height >> 6);

    return 0;
}

int font_text_width(int slot, const char *utf8)
{
    FT_Face f;
    const char *p = utf8;
    int w = 0;

    if (slot < 0 || slot >= FONT_MAX_SLOTS || !utf8)
        return 0;

    f = s_face[slot];

    while (*p) {
        uint32_t cp = utf8_next(&p);

        if (cp == '\n')
            break;

        if (FT_Load_Char(f, cp, FT_LOAD_DEFAULT) != 0)
            continue;

        w += (int)(f->glyph->advance.x >> 6);
    }

    return w;
}

int font_fit_chars(int slot, const char *utf8, int max_px)
{
    FT_Face f;
    const char *p = utf8;
    int w = 0, n = 0;

    if (slot < 0 || slot >= FONT_MAX_SLOTS || !utf8)
        return 0;

    f = s_face[slot];

    while (*p) {
        uint32_t cp = utf8_next(&p);
        int adv;

        if (cp == '\n')
            break;

        if (FT_Load_Char(f, cp, FT_LOAD_DEFAULT) != 0)
            continue;

        adv = (int)(f->glyph->advance.x >> 6);
        if (w + adv > max_px)
            break;

        w += adv;
        n++;
    }

    return n;
}

void font_draw(int slot, int x, int y_top, const char *utf8, uint32_t color)
{
    FT_Face f;
    const char *p = utf8;
    int pen = x;
    int base;

    if (slot < 0 || slot >= FONT_MAX_SLOTS || !utf8)
        return;

    f = s_face[slot];
    base = y_top + face_ascender(f);

    while (*p) {
        uint32_t cp = utf8_next(&p);
        FT_GlyphSlot g;

        if (cp == '\n')
            break;

        if (FT_Load_Char(f, cp, FT_LOAD_DEFAULT) != 0)
            continue;

        if (FT_Render_Glyph(f->glyph, ft_render_mode_normal) != 0)
            continue;

        g = f->glyph;

        if (g->bitmap.buffer && g->bitmap.width > 0 && g->bitmap.rows > 0) {
            gfx_blit_alpha(pen + g->bitmap_left, base - g->bitmap_top,
                           g->bitmap.buffer, (int)g->bitmap.width, (int)g->bitmap.rows,
                           (int)g->bitmap.pitch, color);
        }

        pen += (int)(g->advance.x >> 6);
    }
}
