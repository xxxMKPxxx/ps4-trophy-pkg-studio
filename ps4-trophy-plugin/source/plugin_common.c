#include "plugin_common.h"

#include <string.h>
#include <stdio.h>
#include <stdarg.h>

/* 插件自己的日志，直接落文件 —— 不依赖 klog 是否开着、也不用抓 TCP。
 * 优先写 /data/GoldHEN/，不可写就退到 /data/。 */
void plog(const char *fmt, ...)
{
    static const char *paths[] = {
        "/data/GoldHEN/trophy_unlocker.log",
        "/data/trophy_unlocker.log",
    };

    FILE *f = NULL;
    for (int i = 0; i < 2 && !f; i++)
        f = fopen(paths[i], "a");
    if (!f)
        return;

    va_list ap;
    va_start(ap, fmt);
    vfprintf(f, fmt, ap);
    va_end(ap);

    fflush(f);
    fclose(f);
}

/* 来自 OSM-Made/PS4-Notify 的做法 */
void NotifyStatic(const char *IconUri, const char *text)
{
    OrbisNotificationRequest Buffer;
    memset(&Buffer, 0, sizeof(Buffer));

    Buffer.type = NotificationRequest;
    Buffer.unk3 = 0;
    Buffer.useIconImageUri = 1;
    Buffer.targetId = -1;
    strncpy(Buffer.message, text, sizeof(Buffer.message) - 1);
    strncpy(Buffer.iconUri, IconUri, sizeof(Buffer.iconUri) - 1);

    sceKernelSendNotificationRequest(0, &Buffer, sizeof(Buffer), 0);
}

void Notify(const char *IconUri, const char *FMT, ...)
{
    OrbisNotificationRequest Buffer;
    memset(&Buffer, 0, sizeof(Buffer));

    va_list args;
    va_start(args, FMT);
    vsnprintf(Buffer.message, sizeof(Buffer.message), FMT, args);
    va_end(args);

    Buffer.type = NotificationRequest;
    Buffer.unk3 = 0;
    Buffer.useIconImageUri = 1;
    Buffer.targetId = -1;
    strncpy(Buffer.iconUri, IconUri, sizeof(Buffer.iconUri) - 1);

    sceKernelSendNotificationRequest(0, &Buffer, sizeof(Buffer), 0);
}
