#include "unlock_order.h"
#include "plugin_common.h"

#include <stdio.h>
#include <stdlib.h>
#include <string.h>

/* 顺序文件放在 /data/GoldHEN 下（这个目录一定存在，GoldHEN 自己的 plugins 就在里面）。
 * 不建子目录，避免依赖可能没有的 mkdir 符号。 */
#define UO_DIR "/data/GoldHEN"

static unsigned int rd32(const unsigned char *p)
{
    return (unsigned int)p[0] | ((unsigned int)p[1] << 8)
         | ((unsigned int)p[2] << 16) | ((unsigned int)p[3] << 24);
}

static int order_path(char *buf, int len, const char *tid)
{
    snprintf(buf, (size_t)len, UO_DIR "/trophy_order_%s.csv", tid);
    return 0;
}

/* param.sfo 是小端：magic 0x46535000（\0PSF）
 * 头 20 字节：magic, version, key_start, data_start, count
 * 每项 16 字节：key_off(u16), fmt(u16), len(u32), maxlen(u32), data_off(u32)
 */
int uo_title_id(char *out, int len)
{
    const char *paths[2] = { "/app0/sce_sys/param.sfo", "/app0/sce_sys/param.sfo" };

    for (int k = 0; k < 1; k++) {
        FILE *f = fopen(paths[k], "rb");
        if (!f) {
            debug_printf("title_id: 打不开 %s\n", paths[k]);
            continue;
        }

        unsigned char hdr[20];
        if (fread(hdr, 1, sizeof(hdr), f) != sizeof(hdr) || rd32(hdr) != 0x46535000u) {
            debug_printf("title_id: 不是 param.sfo\n");
            fclose(f);
            continue;
        }

        unsigned keyStart = rd32(hdr + 8);
        unsigned dataStart = rd32(hdr + 12);
        unsigned count = rd32(hdr + 16);

        if (count > 512) {
            fclose(f);
            continue;
        }

        for (unsigned i = 0; i < count; i++) {
            unsigned char e[16];
            if (fseek(f, (long)(20 + i * 16), SEEK_SET) != 0)
                break;
            if (fread(e, 1, sizeof(e), f) != sizeof(e))
                break;

            unsigned keyOff = (unsigned)e[0] | ((unsigned)e[1] << 8);
            unsigned vlen = rd32(e + 4);
            unsigned doff = rd32(e + 12);

            if (vlen == 0 || vlen > 64)
                continue;

            char key[64];
            if (fseek(f, (long)(keyStart + keyOff), SEEK_SET) != 0)
                break;
            if (!fgets(key, (int)sizeof(key), f))
                break;

            size_t kl = strlen(key);
            while (kl > 0 && (key[kl - 1] == '\n' || key[kl - 1] == '\r'))
                key[--kl] = '\0';

            if (strcmp(key, "TITLE_ID") != 0)
                continue;

            unsigned char val[64];
            if (fseek(f, (long)(dataStart + doff), SEEK_SET) != 0)
                break;
            if (fread(val, 1, vlen, f) != vlen)
                break;

            int n = (int)vlen;
            while (n > 0 && val[n - 1] == 0)
                n--;
            if (n >= len)
                n = len - 1;

            memcpy(out, val, (size_t)n);
            out[n] = '\0';
            fclose(f);

            debug_printf("title_id: %s\n", out);
            return 0;
        }

        fclose(f);
    }

    out[0] = '\0';
    return -1;
}

int uo_load(const char *tid, int *ids, int *delays, int max)
{
    if (!tid || !tid[0])
        return 0;

    char path[256];
    order_path(path, (int)sizeof(path), tid);

    FILE *f = fopen(path, "r");
    if (!f) {
        debug_printf("order: %s 不存在，按默认顺序解锁\n", path);
        return 0;
    }

    int n = 0;
    char line[256];

    while (fgets(line, (int)sizeof(line), f) && n < max) {
        char *p = line;
        while (*p == ' ' || *p == '\t')
            p++;

        if (*p == '#' || *p == '\r' || *p == '\n' || *p == '\0')
            continue;

        int id = atoi(p);
        char *comma = strchr(p, ',');
        int delay = comma ? atoi(comma + 1) : 1500;

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

    fclose(f);
    debug_printf("order: 读到 %d 条 (%s)\n", n, path);
    return n;
}

int uo_write_template(const char *tid, int count, int default_delay_ms)
{
    if (!tid || !tid[0] || count <= 0)
        return -1;

    char path[256];
    order_path(path, (int)sizeof(path), tid);

    /* 已经存在就不动它，免得把手改的顺序覆盖掉 */
    FILE *chk = fopen(path, "r");
    if (chk) {
        fclose(chk);
        debug_printf("order: %s 已存在，不覆盖\n", path);
        return 1;
    }

    FILE *f = fopen(path, "w");
    if (!f) {
        debug_printf("order: 写不了 %s\n", path);
        return -1;
    }

    fprintf(f, "# %s 的奖杯解锁顺序\n", tid);
    fprintf(f, "# 一行一个：奖杯ID,延迟毫秒（延迟 = 解锁这个之后等多久做下一个）\n");
    fprintf(f, "# 0 号是白金，默认放在最后；想改顺序直接改这个文件，下次「批量解锁」生效\n");
    fprintf(f, "# 想跳过某个奖杯，把那一行删掉或前面加 #\n");

    for (int id = 1; id < count; id++)
        fprintf(f, "%d,%d\n", id, default_delay_ms);

    if (count > 0)
        fprintf(f, "0,%d\n", default_delay_ms + 500);

    fclose(f);

    debug_printf("order: 已生成模板 %s（%d 条，延迟 %d ms）\n", path, count, default_delay_ms);
    return 0;
}
