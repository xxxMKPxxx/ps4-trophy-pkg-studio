#include "plugin_common.h"

#include <string.h>
#include <stdio.h>

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
