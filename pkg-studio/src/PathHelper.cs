using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace PkgStudio
{
    /// <summary>
    /// orbis-pub-cmd 是老式 ANSI 工具，对两类路径很敏感：
    ///   1) UNC 路径 \\host\share\... —— 它自己解析 "pkg:内部路径" 时会被冒号搞乱，
    ///      报 "in_path or out_path is invalid"。
    ///   2) 含中文的目录名 —— 只能靠 8.3 短名绕开。
    /// 这里做的都是“换一种写法指向同一个文件”，不复制任何数据。
    /// </summary>
    internal static class PathHelper
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint GetShortPathNameW(string longPath, StringBuilder shortPath, uint bufferLength);

        private static readonly List<string> MappedDrives = new List<string>();

        static PathHelper()
        {
            // 程序退出时把临时映射的盘符删掉，不留痕迹
            AppDomain.CurrentDomain.ProcessExit += delegate { UnmapAll(); };
        }

        public static bool IsUnc(string p)
        {
            return p != null && p.StartsWith("\\\\", StringComparison.Ordinal);
        }

        public static bool HasNonAscii(string p)
        {
            if (p == null) return false;
            foreach (char c in p)
                if (c > 127)
                    return true;
            return false;
        }

        /// <summary>
        /// 把路径换成 orbis 能接受的写法。返回的路径指向同一个文件，内容不动。
        /// how 会说明做了什么，写进日志方便排查。
        /// </summary>
        public static string ForOrbis(string pkgPath, out string how)
        {
            how = "原样";

            // 已经是普通 ASCII 路径，直接用
            if (!IsUnc(pkgPath) && !HasNonAscii(pkgPath))
                return pkgPath;

            string current = pkgPath;

            // 第一步：UNC → 盘符（去掉 \\host\share 前缀，冒号问题随之消失）
            if (IsUnc(current))
            {
                string mapped = TryMapToDrive(current);
                if (mapped != null)
                {
                    current = mapped;
                    how = "映射盘符";
                }
            }

            // 第二步：含中文 → 8.3 短名（全 ASCII）
            if (HasNonAscii(current))
            {
                string sh = TryShortPath(current);
                if (sh != null)
                {
                    current = sh;
                    how = how == "映射盘符" ? "映射盘符 + 短路径" : "短路径";
                }
            }

            return current;
        }

        /// <summary>\\host\share\a\b.pkg → Z:\a\b.pkg（临时映射，退出时删除）</summary>
        private static string TryMapToDrive(string unc)
        {
            try
            {
                string body = unc.Substring(2);
                int sep = body.IndexOf('\\');
                if (sep < 0)
                    return null;

                int sep2 = body.IndexOf('\\', sep + 1);
                string share = @"\\" + (sep2 < 0 ? body : body.Substring(0, sep2));
                string rest = sep2 < 0 ? "" : body.Substring(sep2);

                string letter = null;
                foreach (char c in "ZYXWVUTSRQPON")
                {
                    string cand = c + ":";
                    if (!Directory.Exists(cand + "\\"))
                    {
                        letter = cand;
                        break;
                    }
                }
                if (letter == null)
                    return null;

                string args = "use " + letter + " \"" + share + "\" /persistent:no";
                string outp = RunCmd(args);
                if (outp == null || !Directory.Exists(letter + "\\"))
                {
                    Log.Write("映射盘符失败: " + share + " -> " + letter + " : " + (outp ?? "(无输出)"));
                    return null;
                }

                if (!MappedDrives.Contains(letter))
                    MappedDrives.Add(letter);

                string result = letter + rest;
                Log.Write("已临时映射 " + letter + " -> " + share + "（退出时删除）");
                Log.Write("路径改写: " + unc + "  ->  " + result);
                return result;
            }
            catch (Exception ex)
            {
                Log.Write("映射盘符异常: " + ex.Message);
                return null;
            }
        }

        /// <summary>取 8.3 短名，并要求结果全是 ASCII。</summary>
        private static string TryShortPath(string path)
        {
            try
            {
                if (!File.Exists(path))
                    return null;

                StringBuilder sb = new StringBuilder(1024);
                uint n = GetShortPathNameW(path, sb, (uint)sb.Capacity);
                if (n == 0 || n >= sb.Capacity)
                    return null;

                string s = sb.ToString();
                if (HasNonAscii(s) || s.IndexOf('~') < 0)
                    return null;

                return File.Exists(s) ? s : null;
            }
            catch
            {
                return null;
            }
        }

        private static string RunCmd(string args)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo("cmd.exe", "/c net " + args);
                psi.UseShellExecute = false;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.CreateNoWindow = true;

                using (Process p = Process.Start(psi))
                {
                    string o = p.StandardOutput.ReadToEnd();
                    string e = p.StandardError.ReadToEnd();
                    p.WaitForExit(15000);
                    string all = (o + " " + e).Trim();
                    return p.ExitCode == 0 ? all : (all.Length > 0 ? all : null);
                }
            }
            catch
            {
                return null;
            }
        }

        private static void UnmapAll()
        {
            foreach (string d in MappedDrives)
            {
                try { RunCmd("use " + d + " /delete /y"); }
                catch { }
            }
            MappedDrives.Clear();
        }
    }
}
