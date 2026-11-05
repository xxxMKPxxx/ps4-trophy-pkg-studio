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

        // 已安装游戏的“真索引”：每装一个游戏这里就有一个 <TITLEID> 目录。
        // appmeta 只是汇总目录，有些游戏在那里没有条目，所以用它来补全列表。
        private static readonly string[] AppDirs = new string[]
        {
            "user/app/",
            "mnt/ext0/user/app/",
            "mnt/ext1/user/app/",
            "mnt/usb0/user/app/",
            "mnt/usb1/user/app/"
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

                // 目录名就是 Title ID，先过滤掉系统应用，省掉大量无用下载
                List<string> targets = new List<string>();
                foreach (string n in names)
                {
                    if (n.Length < 4)
                        continue;
                    if (!n.StartsWith("CUS", StringComparison.OrdinalIgnoreCase))
                        continue;
                    targets.Add(n);
                }

                log("目录 " + d + " 条目 " + names.Count + "，CUS 开头 " + targets.Count);

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

            // ---------- 第二阶段：用 /user/app/ 的索引补全 appmeta 漏掉的游戏 ----------
            // 装了游戏就一定会在 /user/app/<TITLEID>/ 下有个目录（外置盘在 /mnt/ext0/user/app/），
            // 而 appmeta 只是汇总目录，个别游戏在那里没有条目 —— 这就是"列表不全"的来源。
            int before = list.Count;
            List<string> appTids = new List<string>();
            Dictionary<string, string> appPkg = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            HashSet<string> appSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (string d in AppDirs)
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

                int added = 0;
                foreach (string n in names)
                {
                    if (!LooksLikeTitleId(n))
                        continue;
                    if (appSeen.Add(n))
                    {
                        appTids.Add(n);
                        appPkg[n] = url + n + "/app.pkg";   // 游戏本体，元数据缺失时直接读它
                        added++;
                    }
                }

                log("索引 " + d + " 条目 " + names.Count + "，Title ID " + added);
            }

            List<string> missing = new List<string>();
            lock (gate)
            {
                foreach (string t in appTids)
                    if (!seen.Contains(t))
                        missing.Add(t);
            }

            log("user/app 索引 " + appTids.Count + " 个，其中 " + missing.Count + " 个不在 appmeta 列表里，逐个反查元数据…");

            Parallel.ForEach(missing,
                new ParallelOptions { MaxDegreeOfParallelism = Parallelism },
                tid =>
                {
                    try
                    {
                        string meta = FindMeta(root, tid);
                        byte[] param = null;
                        string pkgUrl;
                        appPkg.TryGetValue(tid, out pkgUrl);

                        if (meta != null)
                            param = Download(meta + "param.sfo");

                        // 元数据目录里没有，就直接读那个 app.pkg 的包头和条目表（只几十 KB）
                        if (param == null || param.Length == 0)
                        {
                            if (pkgUrl == null)
                            {
                                Log.Write("补不上 " + tid + "：无处可读");
                                return;
                            }

                            Log.Write("元数据缺失，改从 app.pkg 直读: " + pkgUrl);
                            param = RemotePkg.GetParam(pkgUrl);
                            if (param != null && param.Length > 0)
                                meta = null;     // 其他文件也走远程读
                        }

                        if (param == null || param.Length == 0)
                            return;

                        GameItem it = new GameItem();
                        it.Source = "FTP";
                        it.Origin = meta;            // 有元数据目录就用它，否则打包时走远程读
                        it.RootUrl = root;
                        it.PkgUrl = pkgUrl;
                        it.Param = param;
                        it.Title = Sfo.Get(param, "TITLE");
                        it.TitleId = Sfo.Get(param, "TITLE_ID");
                        it.ContentId = Sfo.Get(param, "CONTENT_ID");
                        it.Category = Sfo.Get(param, "CATEGORY");
                        it.AppVer = Sfo.Get(param, "APP_VER");
                        it.Kind = GameItem.KindOf(it.Category);

                        if (it.Kind == GameKind.Update)
                            return;

                        // 远端的 nptitle/npbind 如果是加密条目，就提前标出来，
                        // 免得用户以为"可打包"，点了才失败。
                        bool? enc = RemotePkg.IsEncrypted(pkgUrl, RemotePkg.IdNptitle);
                        if (enc == true)
                        {
                            it.NeedsLocalPkg = true;
                            Log.Write("标记 " + tid + "：app.pkg 里的 nptitle/npbind 是加密条目，需用本地 pkg 导入");
                        }

                        lock (gate)
                        {
                            if (seen.Add(it.TitleId))
                            {
                                list.Add(it);
                                Log.Write("索引补全: " + it.Title + " / " + it.TitleId + "  元数据在 " + meta);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Write("补全失败 " + tid + " : " + ex.Message);
                    }
                });

            log("索引补全 "+ (list.Count - before) + " 个");

            watch.Stop();
            Log.Write("FTP 扫描完成: " + list.Count + " 个，用时 " + watch.ElapsedMilliseconds + " ms");
            log("扫描完成：" + list.Count + " 个，用时 " + (watch.ElapsedMilliseconds / 1000.0).ToString("0.0") + " 秒");
            return list;
        }

        /// <summary>打包前补齐 nptitle / npbind / trophy。</summary>
        public static void EnsureFiles(GameItem it, Action<string> log)
        {
            if (it.FilesLoaded)
                return;

            string url = string.IsNullOrEmpty(it.Origin)
                ? null
                : (it.Origin.EndsWith("/") ? it.Origin : it.Origin + "/");

            // appmeta 里可能只有 param.sfo，另外三个文件要靠 app.pkg —— 先把地址找出来
            if (string.IsNullOrEmpty(it.PkgUrl) && !string.IsNullOrEmpty(it.RootUrl))
                it.PkgUrl = FindPkgUrl(it.RootUrl, it.TitleId);

            if (url != null)
            {
                it.Nptitle = Download(url + "nptitle.dat");
                it.Npbind = Download(url + "npbind.dat");
            }

            // 元数据目录里没有 → 直接从那个 app.pkg 里读（只读包头和条目表，几十 KB）
            bool encTitle = false, encBind = false;

            if ((it.Nptitle == null || it.Nptitle.Length == 0) && !string.IsNullOrEmpty(it.PkgUrl))
            {
                Log.Write("元数据目录没有 nptitle.dat，改从 app.pkg 读");
                it.Nptitle = RemotePkg.GetSpecial(it.PkgUrl, RemotePkg.IdNptitle, out encTitle);
            }
            if ((it.Npbind == null || it.Npbind.Length == 0) && !string.IsNullOrEmpty(it.PkgUrl))
            {
                Log.Write("元数据目录没有 npbind.dat，改从 app.pkg 读");
                it.Npbind = RemotePkg.GetSpecial(it.PkgUrl, RemotePkg.IdNpbind, out encBind);
            }

            Log.Write("补齐 " + it.TitleId + ": nptitle=" + (it.Nptitle == null ? "无" : it.Nptitle.Length + "B")
                + " npbind=" + (it.Npbind == null ? "无" : it.Npbind.Length + "B")
                + (encTitle || encBind ? "（加密条目）" : ""));

            // 加密条目拿到的只是密文，没有密钥解不开 —— 直接说清楚，别让它走到"找不到 NPWR"那种误导人的报错
            if (encTitle || encBind)
            {
                throw new Exception("这个游戏的 nptitle.dat / npbind.dat 在 app.pkg 里是加密条目"
                    + "（flags1=0x80000000，密钥索引在 flags2 里），appmeta 目录里也没有明文版本，"
                    + "所以远程拿不到。\r\n\r\n"
                    + "解决办法：把这个游戏的 pkg 拖进「导入 PKG」，本地解析走的是 PS4_Tools 的解密实现，可以正常读出。");
            }

            if (it.Npbind == null || it.Npbind.Length == 0)
                throw new Exception("既没有元数据目录，也没能从 " + (it.PkgUrl ?? "(无 app.pkg)") + " 里读到 npbind.dat");
            if (it.Nptitle == null || it.Nptitle.Length == 0)
                throw new Exception("既没有元数据目录，也没能从 " + (it.PkgUrl ?? "(无 app.pkg)") + " 里读到 nptitle.dat");

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

            // 最后一条路：直接从 app.pkg 里取 trophy00.trp
            if ((it.Trophy == null || it.Trophy.Length == 0) && !string.IsNullOrEmpty(it.PkgUrl))
            {
                Log.Write("奖杯目录取不到，改从 app.pkg 读 trophy00.trp");
                it.Trophy = RemotePkg.GetByName(it.PkgUrl, "trophy00.trp");
            }

            if (it.Trophy == null || it.Trophy.Length == 0)
                throw new Exception("TROPHY.TRP 取不到，试过 " + string.Join(" 和 ", ids)
                    + "（远端 " + root + "user/trophy/conf/），也没能从 app.pkg 里读到");

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

        /// <summary>在各盘找这个 Title ID 的 app.pkg，返回第一个存在的地址。</summary>
        private static string FindPkgUrl(string root, string tid)
        {
            if (string.IsNullOrEmpty(root) || string.IsNullOrEmpty(tid))
                return null;

            string[] bases = new string[]
            {
                "user/app/",
                "mnt/ext0/user/app/",
                "mnt/ext1/user/app/",
                "mnt/usb0/user/app/",
                "mnt/usb1/user/app/"
            };

            foreach (string b in bases)
            {
                string u = root + b + tid + "/app.pkg";
                if (ProbeSize(u) > 0)
                {
                    Log.Write("找到 app.pkg: " + u);
                    return u;
                }
            }
            return null;
        }

        /// <summary>FTP SIZE，失败返回 -1。</summary>
        private static long ProbeSize(string url)
        {
            try
            {
                FtpWebRequest req = (FtpWebRequest)WebRequest.Create(url);
                req.Credentials = new NetworkCredential("anonymous", "anonymous@");
                req.Method = WebRequestMethods.Ftp.GetFileSize;
                req.UsePassive = true;
                req.UseBinary = true;
                req.KeepAlive = false;
                req.Timeout = TimeoutMs;
                req.ReadWriteTimeout = TimeoutMs;

                using (FtpWebResponse resp = (FtpWebResponse)req.GetResponse())
                    return resp.ContentLength;
            }
            catch
            {
                return -1;
            }
        }

        /// <summary>目录名像不像 Title ID：CUSA04893 / PPSA01234，9 位，前 4 字母后 5 数字。系统应用 NPXS 排除。</summary>
        private static bool LooksLikeTitleId(string n)
        {
            if (n == null || n.Length != 9)
                return false;
            if (n.StartsWith("NP", StringComparison.OrdinalIgnoreCase))
                return false;

            for (int i = 0; i < 4; i++)
            {
                char c = n[i];
                if (!((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z')))
                    return false;
            }
            for (int i = 4; i < 9; i++)
            {
                if (n[i] < '0' || n[i] > '9')
                    return false;
            }
            return true;
        }

        /// <summary>按可能的位置找一个含 param.sfo 的目录，返回该目录的 URL（带结尾斜杠）。</summary>
        private static string FindMeta(string root, string tid)
        {
            string[] bases = new string[]
            {
                root + "user/appmeta/" + tid + "/",
                root + "system_data/priv/appmeta/" + tid + "/",
                root + "mnt/ext0/user/appmeta/" + tid + "/",
                root + "mnt/ext1/user/appmeta/" + tid + "/",
                root + "user/app/" + tid + "/sce_sys/",
                root + "mnt/ext0/user/app/" + tid + "/sce_sys/",
                root + "mnt/ext1/user/app/" + tid + "/sce_sys/",
                root + "mnt/usb0/user/app/" + tid + "/sce_sys/",
                root + "mnt/usb1/user/app/" + tid + "/sce_sys/"
            };

            foreach (string b in bases)
            {
                byte[] p = Download(b + "param.sfo");
                if (p != null && p.Length > 0)
                    return b;
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
