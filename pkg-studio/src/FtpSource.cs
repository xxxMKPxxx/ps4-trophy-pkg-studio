using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading.Tasks;

namespace PkgStudio
{
    /// <summary>
    /// 从 PS4 的 FTP（GoldHEN 内置，端口 2121）扫已安装游戏。
    ///
    /// 速度上的三个关键点：
    ///  1. appmeta 下的目录名本身就是 Title ID，先用 CUS 前缀过滤，
    ///     不去下载那一堆 NPXS 系统应用的 param.sfo；
    ///  2. param.sfo 并发下载（原来一个个来，几百个游戏要等很久）；
    ///  3. 超时 20s→12s、重试 3→2 次，小文件失败通常是真的没有。
    /// </summary>
    internal static class FtpSource
    {
        private static readonly string[] Dirs = new string[]
        {
            "system_data/priv/appmeta/",
            "user/appmeta/",
            "mnt/ext0/user/appmeta/",
            "mnt/ext1/user/appmeta/",
            "mnt/usb0/user/appmeta/",
            "mnt/usb1/user/appmeta/"
        };

        private const int Parallelism = 8;
        private const int TimeoutMs = 12000;

        public static List<GameItem> Scan(string host, Action<string> log)
        {
            ServicePointManager.DefaultConnectionLimit = 32;

            string root = "ftp://" + host + ":2121/";
            List<GameItem> list = new List<GameItem>();
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            object gate = new object();

            System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();

            foreach (string d in Dirs)
            {
                string url = root + d;
                List<string> names;
                try
                {
                    names = ListDirs(url);
                }
                catch (Exception ex)
                {
                    log("目录不可用 " + d + " : " + ex.Message);
                    continue;
                }

                // 目录名就是 Title ID。CUS 开头 = 游戏本体；
                // XDPX 开头 = 之前生成的解锁器 —— 它的 appmeta 里 param.sfo/nptitle/npbind
                // 齐全，npbind 的 NPWR 就指向原游戏的奖杯集，TRP 也能从 conf/<NPWR>/ 取到，
                // 所以旧解锁器可以直接当「提取源」再出新包（原游戏 PKG 删了也不影响）。
                List<string> targets = new List<string>();
                foreach (string n in names)
                {
                    if (n.Length < 4)
                        continue;
                    bool cand = n.StartsWith("CUS", StringComparison.OrdinalIgnoreCase)
                             || n.StartsWith("XDPX", StringComparison.OrdinalIgnoreCase);
                    if (!cand)
                        continue;
                    targets.Add(n);
                }

                log("目录 " + d + " 条目 " + names.Count + "，CUS/XDPX 开头 " + targets.Count);

                Parallel.ForEach(targets,
                    new ParallelOptions { MaxDegreeOfParallelism = Parallelism },
                    n =>
                    {
                        try
                        {
                            byte[] param = Download(url + n + "/param.sfo");
                            if (param == null || param.Length == 0)
                                throw new Exception("param.sfo 取不到");

                            GameItem it = new GameItem();
                            it.Source = "FTP";
                            it.Origin = url + n;
                            it.RootUrl = root;
                            it.Param = param;
                            it.Title = Sfo.Get(param, "TITLE");
                            it.TitleId = Sfo.Get(param, "TITLE_ID");
                            it.ContentId = Sfo.Get(param, "CONTENT_ID");
                            it.Category = Sfo.Get(param, "CATEGORY");
                            it.AppVer = Sfo.Get(param, "APP_VER");
                            it.Kind = GameItem.KindOf(it.Category);

                            if (string.IsNullOrEmpty(it.TitleId))
                                throw new Exception("没有 TITLE_ID");
                            if (it.Kind == GameKind.Update)
                                return;

                            lock (gate)
                            {
                                if (seen.Add(it.TitleId))
                                    list.Add(it);
                            }
                        }
                        catch (Exception ex)
                        {
                            lock (gate) { }
                            log("跳过 " + n + " : " + ex.Message);
                        }
                    });
            }

            watch.Stop();
            Log.Write("FTP 扫描完成: " + list.Count + " 个，用时 " + watch.ElapsedMilliseconds + " ms");
            log("扫描完成：" + list.Count + " 个，用时 " + (watch.ElapsedMilliseconds / 1000.0).ToString("0.0") + " 秒");
            return list;
        }

        /// <summary>打包前补齐 nptitle / npbind / trophy。</summary>
        public static void EnsureFiles(GameItem it, Action<string> log)
        {
            string url = it.Origin.EndsWith("/") ? it.Origin : it.Origin + "/";

            // 游戏图标（做解锁器图标的底图）。只在打包时下载，扫描阶段不下线图，
            // 免得几百个游戏各多一次 FTP 请求。
            if (it.Icon0 == null || it.Icon0.Length == 0)
                it.Icon0 = Download(url + "icon0.png");

            if (it.FilesLoaded)
                return;

            it.Nptitle = Download(url + "nptitle.dat");
            it.Npbind = Download(url + "npbind.dat");

            Log.Write("补齐 " + it.TitleId + ": nptitle=" + (it.Nptitle == null ? "无" : it.Nptitle.Length + "B")
                + " npbind=" + (it.Npbind == null ? "无" : it.Npbind.Length + "B"));

            if (it.Npbind == null || it.Npbind.Length == 0)
                throw new Exception("远端 " + url + " 下没有 npbind.dat（这个游戏可能没装奖杯数据）");
            if (it.Nptitle == null || it.Nptitle.Length == 0)
                throw new Exception("远端 " + url + " 下没有 nptitle.dat");

            string npwr = FindNpwr(it.Npbind);
            if (npwr == null)
                npwr = FindNpwr(it.Nptitle);
            if (npwr == null)
                throw new Exception("npbind.dat 里找不到 NPWRxxxxx");

            string root = it.RootUrl;
            if (string.IsNullOrEmpty(root))
                throw new Exception("缺少 FTP 根路径（请重新扫描一次）");

            // 奖杯在根目录的 /user/trophy/conf/<NPWR>/TROPHY.TRP，不在 appmeta 下面
            string[] ids = new string[] { npwr, npwr + "_00" };
            foreach (string id in ids)
            {
                byte[] t = Download(root + "user/trophy/conf/" + id + "/TROPHY.TRP");
                if (t != null && t.Length > 0)
                {
                    it.Trophy = t;
                    Log.Write("奖杯文件取自 " + id + " (" + t.Length + "B)");
                    break;
                }
            }

            if (it.Trophy == null || it.Trophy.Length == 0)
                throw new Exception("TROPHY.TRP 取不到，试过 " + string.Join(" 和 ", ids)
                    + "（远端 " + root + "user/trophy/conf/）");

            log("已补齐 " + it.TitleId + " 的奖杯文件 (" + npwr + ")");
        }

        private static string FindNpwr(byte[] data)
        {
            if (data == null)
                return null;

            for (int i = 0; i + 9 <= data.Length; i++)
            {
                if (data[i] != 'N' || data[i + 1] != 'P' || data[i + 2] != 'W' || data[i + 3] != 'R')
                    continue;

                bool ok = true;
                for (int j = 4; j < 9; j++)
                {
                    if (data[i + j] < '0' || data[i + j] > '9') { ok = false; break; }
                }
                if (!ok)
                    continue;

                return Encoding.ASCII.GetString(data, i, 9);
            }
            return null;
        }

        /// <summary>把 PS4 上的文件下载到本地；失败返回 false。</summary>
        public static bool TryDownload(string host, string remotePath, string localFile)
        {
            try
            {
                string url = "ftp://" + host + ":2121" + remotePath;
                byte[] d = Download(url);
                if (d == null || d.Length == 0)
                    return false;

                File.WriteAllBytes(localFile, d);
                return true;
            }
            catch (Exception ex)
            {
                Log.Write("下载失败 " + remotePath + " : " + ex.Message);
                return false;
            }
        }

        /// <summary>把本地文件上传到 PS4（GoldHEN FTP 的 STOR）。</summary>
        public static bool Upload(string host, string remotePath, string localFile)
        {
            try
            {
                string url = "ftp://" + host + ":2121" + remotePath;
                byte[] data = File.ReadAllBytes(localFile);

                FtpWebRequest req = (FtpWebRequest)WebRequest.Create(url);
                req.Credentials = new NetworkCredential("anonymous", "anonymous@");
                req.Method = WebRequestMethods.Ftp.UploadFile;
                req.UsePassive = true;
                req.UseBinary = true;
                req.KeepAlive = false;
                req.Timeout = 15000;
                req.ReadWriteTimeout = 15000;

                using (Stream s = req.GetRequestStream())
                    s.Write(data, 0, data.Length);
                using (FtpWebResponse resp = (FtpWebResponse)req.GetResponse())
                {
                    Log.Write("已上传 " + remotePath + "（" + data.Length + "B）：" + resp.StatusDescription.Trim());
                }
                return true;
            }
            catch (Exception ex)
            {
                Log.Write("上传失败 " + remotePath + " : " + ex.Message);
                return false;
            }
        }

        private static List<string> ListDirs(string url)
        {
            List<string> names = new List<string>();

            FtpWebRequest req = (FtpWebRequest)WebRequest.Create(url);
            req.Credentials = new NetworkCredential("anonymous", "anonymous@");
            req.Method = WebRequestMethods.Ftp.ListDirectoryDetails;
            req.UsePassive = true;
            req.UseBinary = true;
            req.KeepAlive = false;
            req.Timeout = TimeoutMs;
            req.ReadWriteTimeout = TimeoutMs;

            using (FtpWebResponse resp = (FtpWebResponse)req.GetResponse())
            using (StreamReader sr = new StreamReader(resp.GetResponseStream()))
            {
                string line;
                while ((line = sr.ReadLine()) != null)
                {
                    if (line.Length == 0)
                        continue;

                    bool isDir = line[0] == 'd' ||
                                 line.StartsWith("type=dir", StringComparison.OrdinalIgnoreCase);
                    if (!isDir)
                        continue;

                    string[] parts = line.Split(new char[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length == 0)
                        continue;

                    names.Add(parts[parts.Length - 1]);
                }
            }
            return names;
        }

        private static byte[] Download(string url)
        {
            for (int attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    FtpWebRequest req = (FtpWebRequest)WebRequest.Create(url);
                    req.Credentials = new NetworkCredential("anonymous", "anonymous@");
                    req.Method = WebRequestMethods.Ftp.DownloadFile;
                    req.UsePassive = true;
                    req.UseBinary = true;
                    req.KeepAlive = false;
                    req.Timeout = TimeoutMs;
                    req.ReadWriteTimeout = TimeoutMs;

                    using (FtpWebResponse resp = (FtpWebResponse)req.GetResponse())
                    using (Stream s = resp.GetResponseStream())
                    using (MemoryStream ms = new MemoryStream())
                    {
                        byte[] buf = new byte[8192];
                        int n;
                        while ((n = s.Read(buf, 0, buf.Length)) > 0)
                            ms.Write(buf, 0, n);
                        return ms.ToArray();
                    }
                }
                catch
                {
                    System.Threading.Thread.Sleep(150);
                }
            }
            return null;
        }
    }
}
