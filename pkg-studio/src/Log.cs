using System;
using System.IO;
using System.Text;

namespace PkgStudio
{
    /// <summary>写日志到 exe 同目录的 pkgstudio.log —— 出问题时直接看这个文件。</summary>
    internal static class Log
    {
        private static readonly object Gate = new object();

        public static string FilePath
        {
            get
            {
                return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "pkgstudio.log");
            }
        }

        public static void Write(string message)
        {
            try
            {
                lock (Gate)
                {
                    File.AppendAllText(FilePath,
                        DateTime.Now.ToString("HH:mm:ss") + "  " + message + Environment.NewLine,
                        Encoding.UTF8);
                }
            }
            catch
            {
                // 日志失败绝不能影响主流程
            }
        }

        public static void Error(string where, Exception ex)
        {
            Write("[" + where + "] " + ex.GetType().Name + ": " + ex.Message);
            if (!string.IsNullOrEmpty(ex.StackTrace))
                Write("    " + ex.StackTrace.Replace("\r\n", "\r\n    "));
        }
    }
}
