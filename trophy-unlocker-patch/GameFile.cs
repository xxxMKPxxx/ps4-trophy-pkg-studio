// GameFile.cs — 在一个文件夹里找出打包需要的四个文件
//
// 手动添加游戏时，用户给的目录结构五花八门：
//   - 直接是四个文件
//   - 解包后的 sce_sys/ 结构
//   - 带 Image0/ 外层
//   - 奖杯文件在 sce_sys/trophy/ 下
// 这里按常见位置依次找，最后再递归兜底。
using System;
using System.IO;

internal static class GameFile
{
    public static string Find(string dir, string name)
    {
        if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(name))
            return null;

        string[] candidates = new string[]
        {
            Path.Combine(dir, name),
            Path.Combine(dir, "sce_sys", name),
            Path.Combine(dir, "trophy", name),
            Path.Combine(dir, "sce_sys", "trophy", name),
            Path.Combine(dir, "Image0", "sce_sys", name),
            Path.Combine(dir, "Image0", "sce_sys", "trophy", name)
        };

        foreach (string c in candidates)
        {
            if (File.Exists(c))
                return c;
        }

        try
        {
            string[] found = Directory.GetFiles(dir, name, SearchOption.AllDirectories);
            if (found.Length > 0)
                return found[0];
        }
        catch
        {
            // 无权限或路径过长，忽略
        }
        return null;
    }
}
