/*
 * main.c - GoldHEN 插件入口
 *
 * 入口签名与 GoldHEN 官方 plugin_template 一致：
 *   g_pluginName / g_pluginDesc / g_pluginAuth / g_pluginVersion
 *   plugin_load(argc, argv) / plugin_unload(argc, argv)
 *   module_start / module_stop
 *
 * 产物 trophy_unlocker.prx 放到 /data/GoldHEN/plugins/，
 * 并在 /data/GoldHEN/plugins.ini 里加一行：
 *   trophy_unlocker
 */
#include <stdint.h>
#include <stddef.h>

#include "plugin_common.h"
#include "trophy_plugin.h"

attr_public const char *g_pluginName    = "trophy_unlocker";
attr_public const char *g_pluginDesc    = "In-game trophy unlocker (real, via NpTrophy)";
attr_public const char *g_pluginAuth    = "";
attr_public uint32_t    g_pluginVersion = 0x00000100;   /* 1.00 */

int32_t attr_public plugin_load(int32_t argc, const char *argv[])
{
    plog("=== plugin_load ver 0x%08X ===\n", g_pluginVersion);
    final_printf("[GoldHEN] %s started, ver 0x%08X\n", g_pluginName, g_pluginVersion);

    if (trophy_plugin_start() != 0) {
        plog("trophy_plugin_start failed\n");
        final_printf("trophy_plugin_start failed\n");
        return -1;
    }

    NotifyStatic(TEX_ICON_SYSTEM, "Trophy Unlocker loaded: L3+R3");
    return 0;
}

int32_t attr_public plugin_unload(int32_t argc, const char *argv[])
{
    trophy_plugin_stop();
    plog("=== plugin_unload ===\n");
    final_printf("[GoldHEN] %s unloaded\n", g_pluginName);
    return 0;
}

s32 attr_module_hidden module_start(s64 argc, const void *args)
{
    (void)argc;
    (void)args;
    return 0;
}

s32 attr_module_hidden module_stop(s64 argc, const void *args)
{
    (void)argc;
    (void)args;
    return 0;
}
