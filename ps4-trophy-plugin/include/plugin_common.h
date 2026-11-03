/*
 * plugin_common.h - GoldHEN 插件公共定义
 *
 * 官方仓库里的同名文件还依赖 git_ver.h（构建时生成）。这里去掉了那部分，
 * 只保留插件真正需要的宏和 Notify，方便单独编译。
 */
#ifndef _PLUGIN_COMMON_H_
#define _PLUGIN_COMMON_H_

#include <stdint.h>
#include <stdarg.h>
#include <stddef.h>

#include <Common.h>          /* GoldHEN SDK：klog / OrbisNotificationRequest */

#define GOLDHEN_PATH      "/data/GoldHEN"
#define TEX_ICON_SYSTEM   "cxml://psnotification/tex_icon_system"

#define attr_module_hidden __attribute__((weak)) __attribute__((visibility("hidden")))
#define attr_public        __attribute__((visibility("default")))

/* 调试期打开：klog 里能看到全部过程 */
#define debug_printf(a, args...) klog("[trophy] " a, ##args)
#define final_printf(a, args...) klog("(%s:%d) " a, __FILE__, __LINE__, ##args)

void NotifyStatic(const char *IconUri, const char *text);
void Notify(const char *IconUri, const char *FMT, ...);

#endif
