using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;

namespace PkgStudio
{
    /// <summary>
    /// orbis-pub-cmd 的运行环境。
    ///
    /// 这个工具需要同目录（或 ext\ 子目录）下的 sc.exe，否则报
    /// "The version of sc.exe is invalid or missing."。sc.exe 打包在模板的
    /// ext.zip 里，而且社区反馈是"相关文件必须放在 ext 文件夹"下，
    /// 所以这里按原样解压 ext.zip（保留内部结构），并在两个位置都放一份
    /// orbis-pub-cmd.exe，运行时先试根目录、失败再试 ext\。
    /// </summary>
    internal static class Orbis
    {
        public static string EnsureTool(string templateDir, string workRoot)
        {
            string tools = Path.Combine(workRoot, "tools");
            Directory.CreateDirectory(tools);

            string src = Path.Combine(templateDir, "orbis-pub-cmd.exe");
            if (!File.Exists(src))
                src = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "orbis-pub-cmd.exe");
            if (!File.Exists(src))
                throw new Exception("找不到 orbis-pub-cmd.exe（模板目录和 exe 目录都没有）");

            string exe = Path.Combine(tools, "orbis-pub-cmd.exe");
            if (!File.Exists(exe) || File.GetLastWriteTimeUtc(src) > File.GetLastWriteTimeUtc(exe))
                File.Copy(src, exe, true);

            string extDir = Path.Combine(tools, "ext");
            string marker = Path.Combine(tools, ".ext_ok");
            if (!File.Exists(marker))
            {
                string ext = Path.Combine(templateDir, "ext.zip");
                if (!File.Exists(ext))
                    ext = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ext.zip");

                if (File.Exists(ext))
                {
                    try
                    {
                        ZipFile.ExtractToDirectory(ext, tools);
                        File.WriteAllText(marker, DateTime.Now.ToString("s"));
                        Log.Write("已解压 ext.zip 到 " + tools
                            + "（ext 子目录=" + Directory.Exists(extDir) + "）");
                    }
                    catch (Exception ex)
                    {
                        Log.Write("解压 ext.zip 失败: " + ex.Message);
                    }
                }
                else
                {
                    Log.Write("警告：模板里没有 ext.zip，sc.exe 可能缺失");
                }
            }

            // 两个位置各放一份，运行时会自动挑能用的那个
            if (Directory.Exists(extDir))
            {
                string altExe = Path.Combine(extDir, "orbis-pub-cmd.exe");
                if (!File.Exists(altExe))
                {
                    try { File.Copy(exe, altExe, true); } catch { }
                }
            }

            return exe;
        }

        public static string Run(string exe, string args, string workDir)
        {
            string result = RunRaw(exe, args, workDir);

            // 典型症状：sc.exe 找不到。换到 ext\ 目录再试一次
            if (result.IndexOf("sc.exe", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                string alt = Path.Combine(workDir, "ext");
                string altExe = Path.Combine(alt, "orbis-pub-cmd.exe");
                if (Directory.Exists(alt) && File.Exists(altExe))
                {
                    Log.Write("首次运行报 sc.exe 问题，改用 ext\\ 目录重试");
                    string second = RunRaw(altExe, args, alt);
                    Log.Write("重试结果: " + (string.IsNullOrEmpty(second) ? "(空)" : second.Trim()));
                    return second;
                }
            }

            return result;
        }

        private static string RunRaw(string exe, string args, string workDir)
        {
            if (!File.Exists(exe))
                throw new Exception("找不到 orbis-pub-cmd.exe: " + exe);

            ProcessStartInfo psi = new ProcessStartInfo(exe, args);
            psi.UseShellExecute = false;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.CreateNoWindow = true;
            psi.WorkingDirectory = workDir;

            using (Process p = Process.Start(psi))
            {
                string o = p.StandardOutput.ReadToEnd();
                string e = p.StandardError.ReadToEnd();
                p.WaitForExit();
                return o + e;
            }
        }
    }
}
