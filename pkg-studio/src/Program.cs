using System;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace PkgStudio
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            // 兜底：任何未捕获异常（包括 BeginInvoke 回调里的）都写日志再显示，
            // 不再悄悄闪退——用户能拿到具体错误，也好定位。
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += delegate(object sender, ThreadExceptionEventArgs e)
            {
                CrashLog("UI 线程异常", e.Exception);
                MessageBox.Show("发生错误（已记录到 " + CrashPath + "）：\n\n" + e.Exception.Message,
                    "PkgStudio 错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
            };
            AppDomain.CurrentDomain.UnhandledException += delegate(object sender, UnhandledExceptionEventArgs e)
            {
                CrashLog("后台线程异常", e.ExceptionObject as Exception);
            };

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }

        private static string CrashPath
        {
            get { return Path.Combine(Path.GetTempPath(), "pkgstudio-crash.log"); }
        }

        private static void CrashLog(string where, Exception ex)
        {
            try
            {
                string msg = "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "] " + where +
                             "\n" + (ex == null ? "(null)" : ex.ToString()) + "\n\n";
                File.AppendAllText(CrashPath, msg);
            }
            catch { }
        }
    }
}
