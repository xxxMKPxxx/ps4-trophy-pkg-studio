using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;

namespace PkgStudio
{
    /// <summary>
    /// 从 PS4 的 FTP（GoldHEN 内置，端口 2121）扫已安装游戏。
    /// 只下 param.sfo 用来做分类，真正打包时再补另外三个文件。
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

        public static List<GameItem> Scan(string host, Action<string> log)
        {
            List<GameItem> list = new List<GameItem>();
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string root = "ftp://" + host + ":2121/";

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

                log("目录 " + d + " 条目 " + names.Count);
                foreach (string n in names)
                {
                    if (n == "." || n == "..")
                        continue;

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

                        // 只要 CUS 开头的零售 Title ID；更新包不列出来
                        if (!it.TitleId.StartsWith("CUS", StringComparison.OrdinalIgnoreCase))
                            continue;
                        if (it.Kind == GameKind.Update)
                            continue;

                        if (!seen.Add(it.TitleId))
                            continue;

                        list.Add(it);
                    }
                    catch (Exception ex)
                    {
                        log("跳过 " + n + " : " + ex.Message);
                    }
                }
            }
            return list;
        }

        /// <summary>打包前补齐 nptitle / npbind / trophy。</summary>
        public static void EnsureFiles(GameItem it, Action<string> log)
        {
            if (it.FilesLoaded)
                return;

            string url = it.Origin.EndsWith("/") ? it.Origin : it.Origin + "/";

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

            // 奖杯不在 appmeta 下面，而是根目录的 /user/trophy/conf/<NPWR>/TROPHY.TRP
            // 目录名可能是 NPWR12345 或 NPWR12345_00，两种都试
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

        private static List<string> ListDirs(string url)
        {
            List<string> names = new List<string>();

            FtpWebRequest req = (FtpWebRequest)WebRequest.Create(url);
            req.Credentials = new NetworkCredential("anonymous", "anonymous@");
            req.Method = WebRequestMethods.Ftp.ListDirectoryDetails;
            req.UsePassive = true;
            req.UseBinary = true;
            req.Timeout = 20000;
            req.ReadWriteTimeout = 20000;

            using (FtpWebResponse resp = (FtpWebResponse)req.GetResponse())
            using (StreamReader sr = new StreamReader(resp.GetResponseStream()))
            {
                string line;
                while ((line = sr.ReadLine()) != null)
                {
                    if (line.Length == 0)
                        continue;
                    // 只认目录行；ls -l 的 d，以及 MLSD 的 type=dir
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
            for (int attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    FtpWebRequest req = (FtpWebRequest)WebRequest.Create(url);
                    req.Credentials = new NetworkCredential("anonymous", "anonymous@");
                    req.Method = WebRequestMethods.Ftp.DownloadFile;
                    req.UsePassive = true;
                    req.UseBinary = true;
                    req.Timeout = 20000;
                    req.ReadWriteTimeout = 20000;

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
                    System.Threading.Thread.Sleep(300);
                }
            }
            return null;
        }
    }
}
