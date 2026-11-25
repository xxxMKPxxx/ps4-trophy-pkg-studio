using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace PkgStudio
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            // 诊断模式：PkgStudio.exe --probe <pkg路径> <结果文件>
            // 不启动界面，只跑一遍 PKG 读取并把结果写到文件，方便验证大包。
            string[] args = Environment.GetCommandLineArgs();
            if (args.Length >= 3 && args[1] == "--probe")
            {
                Probe(args[2], args[3]);
                return;
            }

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

        private static void Probe(string pkg, string outFile)
        {
            StringBuilder sb = new StringBuilder();
            Stopwatch watch = Stopwatch.StartNew();
            try
            {
                GameItem it = new GameItem { Origin = pkg, Source = "PKG" };
                bool ok = PkgFast.Fill(it);
                sb.AppendLine("ok=" + ok);
                sb.AppendLine("elapsed_ms=" + watch.ElapsedMilliseconds);
                sb.AppendLine("title=" + it.Title);
                sb.AppendLine("title_id=" + it.TitleId);
                sb.AppendLine("category=" + it.Category);
                sb.AppendLine("files_loaded=" + it.FilesLoaded);
                sb.AppendLine("param=" + (it.Param == null ? "null" : it.Param.Length.ToString()));
                sb.AppendLine("nptitle=" + (it.Nptitle == null ? "null" : it.Nptitle.Length.ToString()));
                sb.AppendLine("npbind=" + (it.Npbind == null ? "null" : it.Npbind.Length.ToString()));
                sb.AppendLine("trophy=" + (it.Trophy == null ? "null" : it.Trophy.Length.ToString()));
                sb.AppendLine("trophies=" + (it.Trophies == null ? 0 : it.Trophies.Count));
                sb.AppendLine("sfo_magic=" + (it.Param == null ? "-" :
                    Encoding.ASCII.GetString(it.Param, 0, Math.Min(4, it.Param.Length))));
                sb.AppendLine("nptitle_head=" + Hex(it.Nptitle, 8));
                sb.AppendLine("npbind_head=" + Hex(it.Npbind, 8));
                sb.AppendLine("trophy_head=" + Hex(it.Trophy, 8));
            }
            catch (Exception ex)
            {
                sb.AppendLine("exception=" + ex);
            }
            try { File.WriteAllText(outFile, sb.ToString(), Encoding.UTF8); } catch { }
        }

        private static string Hex(byte[] b, int n)
        {
            if (b == null) return "null";
            StringBuilder s = new StringBuilder();
            for (int i = 0; i < Math.Min(n, b.Length); i++)
                s.Append(b[i].ToString("x2"));
            return s.ToString();
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
