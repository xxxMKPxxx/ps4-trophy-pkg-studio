using System;
using System.Diagnostics;
using System.IO;

namespace PkgStudio
{
    internal static class PkgReader
    {
        private const string Zeros = "00000000000000000000000000000000";

        /// <summary>从 pkg 文件导入：尽量只取 param.sfo，其余文件等打包时再补。</summary>
        public static GameItem FromPkg(string pkgPath, string templateDir, string workRoot)
        {
            if (!File.Exists(pkgPath))
                throw new Exception("文件不存在: " + pkgPath);

            FileInfo fi = new FileInfo(pkgPath);
            string work = Path.Combine(workRoot, "pkgcache", CacheKey(pkgPath, fi));

            string exe = Orbis.EnsureTool(templateDir, workRoot);
            string tools = Path.GetDirectoryName(exe);

            Log.Write("导入 pkg: " + pkgPath + "  (" + (fi.Length / 1024 / 1024) + " MB)");

            Stopwatch watch = Stopwatch.StartNew();
            string sfoPath = FindFile(work, "param.sfo");

            if (sfoPath != null)
            {
                Log.Write("命中缓存: " + work);
            }
            else
            {
                if (!Directory.Exists(work))
                    Directory.CreateDirectory(work);

                // 1) 只取单个文件（最快，成功的话添加就是毫秒级）
                string[] fileSels = new string[]
                {
                    ":Image0/sce_sys/param.sfo",
                    ":sce_sys/param.sfo",
                    ":Sc0/sce_sys/param.sfo"
                };
                sfoPath = TrySelectors(exe, tools, pkgPath, work, fileSels, "file", watch);

                // 2) 取整个 sce_sys 目录
                if (sfoPath == null)
                {
                    string[] dirSels = new string[]
                    {
                        ":Image0/sce_sys",
                        ":sce_sys",
                        ":Sc0/sce_sys"
                    };
                    sfoPath = TrySelectors(exe, tools, pkgPath, work, dirSels, "dir", watch);
                }

                // 3) 最后退回整包（慢，尤其包在网络盘上）
                if (sfoPath == null)
                {
                    Log.Write("选择性解包不可用，退回整包解压（" + (fi.Length / 1024 / 1024) + " MB，会很慢）");
                    watch.Restart();

                    string sub = Path.Combine(work, "full");
                    string outText = Orbis.Run(exe,
                        "img_extract --passcode " + Zeros + " \"" + pkgPath + "\" \"" + sub + "\"", tools);

                    Log.Write("整包解压用时 " + watch.ElapsedMilliseconds + " ms，落地文件 " + CountFiles(sub));
                    sfoPath = FindFile(work, "param.sfo");

                    if (sfoPath == null)
                    {
                        try { Directory.Delete(work, true); } catch { }
                        throw new Exception("解包后找不到 param.sfo。\r\n"
                            + "零售原包没有 passcode 解不开，只能导入 fpkg。\r\n\r\n"
                            + "orbis-pub-cmd 输出：\r\n" + Trim(outText));
                    }
                }
            }

            string dir = Path.GetDirectoryName(sfoPath);

            GameItem it = new GameItem();
            it.Source = "PKG";
            it.Origin = pkgPath;
            it.CacheDir = work;
            it.Param = File.ReadAllBytes(sfoPath);
            it.Title = Sfo.Get(it.Param, "TITLE");
            it.TitleId = Sfo.Get(it.Param, "TITLE_ID");
            it.ContentId = Sfo.Get(it.Param, "CONTENT_ID");
            it.Category = Sfo.Get(it.Param, "CATEGORY");
            it.AppVer = Sfo.Get(it.Param, "APP_VER");
            it.Kind = GameItem.KindOf(it.Category);

            it.Nptitle = TryRead(dir, "nptitle.dat");
            it.Npbind = TryRead(dir, "npbind.dat");
            it.Trophy = TryReadTrophy(dir);

            Log.Write("导入结果(" + watch.ElapsedMilliseconds + " ms): " + it.Title + " / " + it.TitleId
                + " / " + it.Category
                + " / nptitle=" + (it.Nptitle == null ? "无" : it.Nptitle.Length + "B")
                + " / npbind=" + (it.Npbind == null ? "无" : it.Npbind.Length + "B")
                + " / trophy=" + (it.Trophy == null ? "无" : it.Trophy.Length + "B"));

            return it;
        }

        /// <summary>打包前补齐 nptitle / npbind / trophy（导入时可能只拿了 param.sfo）。</summary>
        public static void EnsureFiles(GameItem it, string templateDir, string workRoot, Action<string> log)
        {
            if (it.FilesLoaded)
                return;

            string work = it.CacheDir;
            if (string.IsNullOrEmpty(work))
            {
                FileInfo fi = new FileInfo(it.Origin);
                work = Path.Combine(workRoot, "pkgcache", CacheKey(it.Origin, fi));
                it.CacheDir = work;
            }

            string exe = Orbis.EnsureTool(templateDir, workRoot);
            string tools = Path.GetDirectoryName(exe);

            string sfoPath = FindFile(work, "param.sfo");
            string dir = sfoPath == null ? null : Path.GetDirectoryName(sfoPath);

            if (dir == null || TryRead(dir, "npbind.dat") == null || TryReadTrophy(dir) == null)
            {
                log("补齐 " + it.TitleId + " 的缺件…");
                Stopwatch w = Stopwatch.StartNew();

                string[] dirSels = new string[] { ":Image0/sce_sys", ":sce_sys", ":Sc0/sce_sys" };
                string got = TrySelectors(exe, tools, it.Origin, work, dirSels, "need", w);

                if (got == null)
                {
                    Log.Write("补齐失败，整包解压: " + it.Origin);
                    string sub = Path.Combine(work, "full");
                    Orbis.Run(exe, "img_extract --passcode " + Zeros + " \"" + it.Origin + "\" \"" + sub + "\"", tools);
                }

                sfoPath = FindFile(work, "param.sfo");
                dir = sfoPath == null ? null : Path.GetDirectoryName(sfoPath);
            }

            if (dir == null)
                throw new Exception("缓存里找不到 sce_sys，请重新导入");

            it.Nptitle = TryRead(dir, "nptitle.dat");
            it.Npbind = TryRead(dir, "npbind.dat");
            it.Trophy = TryReadTrophy(dir);

            if (it.Npbind == null) throw new Exception("包里没有 npbind.dat");
            if (it.Nptitle == null) throw new Exception("包里没有 nptitle.dat");
            if (it.Trophy == null) throw new Exception("包里没有 sce_sys/trophy/trophy00.trp");

            log("已补齐 " + it.TitleId);
        }

        private static string TrySelectors(string exe, string tools, string pkgPath, string work,
                                          string[] selectors, string tag, Stopwatch watch)
        {
            int i = 0;
            foreach (string sel in selectors)
            {
                i++;
                string sub = Path.Combine(work, tag + i);
                watch.Restart();

                string outText = Orbis.Run(exe,
                    "img_extract --passcode " + Zeros + " \"" + pkgPath + sel + "\" \"" + sub + "\"", tools);

                int files = CountFiles(sub);
                bool ok = FindFile(work, "param.sfo") != null;

                Log.Write("选择性解包 " + sel + " 用时 " + watch.ElapsedMilliseconds + " ms，落地 " + files
                    + " 个文件，" + (ok ? "成功" : "未拿到 param.sfo"));

                if (ok)
                    return FindFile(work, "param.sfo");

                if (files == 0 && i == 1 && watch.ElapsedMilliseconds > 5000)
                    Log.Write("首次解包偏慢（包可能在网络盘上），后续会快很多");
            }
            return null;
        }

        private static int CountFiles(string dir)
        {
            try
            {
                return Directory.Exists(dir)
                    ? Directory.GetFiles(dir, "*", SearchOption.AllDirectories).Length
                    : 0;
            }
            catch
            {
                return 0;
            }
        }

        /// <summary>缓存键：路径 + 大小 + 修改时间。换包或改文件都会自动失效。</summary>
        private static string CacheKey(string path, FileInfo fi)
        {
            uint h = 2166136261u;
            foreach (char c in path.ToLowerInvariant())
            {
                h ^= c;
                h *= 16777619u;
            }
            return h.ToString("x8") + "_" + fi.Length.ToString("x") + "_" + fi.LastWriteTimeUtc.Ticks.ToString("x");
        }

        private static string Trim(string s)
        {
            if (string.IsNullOrEmpty(s)) return "(无输出)";
            return s.Length > 1200 ? s.Substring(0, 1200) : s;
        }

        public static string Orb(string exe, string args, string workDir)
        {
            return Orbis.Run(exe, args, workDir);
        }

        public static string FindFile(string root, string name)
        {
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
                return null;

            string[] direct = Directory.GetFiles(root, name, SearchOption.AllDirectories);
            return direct.Length > 0 ? direct[0] : null;
        }

        public static string FindDir(string root, string name)
        {
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
                return null;

            string[] direct = Directory.GetDirectories(root, name, SearchOption.AllDirectories);
            return direct.Length > 0 ? direct[0] : null;
        }

        private static byte[] TryRead(string dir, string name)
        {
            string p = Path.Combine(dir, name);
            return File.Exists(p) ? File.ReadAllBytes(p) : null;
        }

        private static byte[] TryReadTrophy(string dir)
        {
            string p = Path.Combine(dir, "trophy", "trophy00.trp");
            if (File.Exists(p)) return File.ReadAllBytes(p);

            p = Path.Combine(dir, "trophy00.trp");
            if (File.Exists(p)) return File.ReadAllBytes(p);

            string found = FindFile(dir, "trophy00.trp");
            return found != null ? File.ReadAllBytes(found) : null;
        }
    }
}
