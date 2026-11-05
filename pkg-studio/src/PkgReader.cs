using System;
using System.Diagnostics;
using System.IO;

namespace PkgStudio
{
    internal static class PkgReader
    {
        /// <summary>从 pkg 文件导入：解包 → 读 param.sfo 和另外三个文件。</summary>
        public static GameItem FromPkg(string pkgPath, string templateDir, string workRoot)
        {
            if (!File.Exists(pkgPath))
                throw new Exception("文件不存在: " + pkgPath);

            FileInfo fi = new FileInfo(pkgPath);
            string key = CacheKey(pkgPath, fi);
            string work = Path.Combine(workRoot, "pkgcache", key);

            string exe = Orbis.EnsureTool(templateDir, workRoot);
            string tools = Path.GetDirectoryName(exe);

            const string Zeros = "00000000000000000000000000000000";
            Log.Write("导入 pkg: " + pkgPath + "  (" + (fi.Length / 1024 / 1024) + " MB)");

            Stopwatch watch = Stopwatch.StartNew();

            // 缓存命中就直接用，不再碰 orbis-pub-cmd
            string sfoPath = FindFile(work, "param.sfo");
            if (sfoPath != null)
            {
                Log.Write("命中缓存: " + work);
            }
            else
            {
                if (!Directory.Exists(work))
                    Directory.CreateDirectory(work);

                string lastOut = null;

                // 只解 sce_sys（几 MB），不要整包解压（可能几十 GB）。
                // 写法来自 orbis-pub-cmd 的 "<pkg>:<内部路径>" 选择性解包；
                // 不同版本内部路径不同，Image0 最常见，其次 Sc0。
                string[] selectors = new string[] { ":Image0/sce_sys", ":sce_sys", ":Sc0/sce_sys" };
                int n = 0;

                foreach (string sel in selectors)
                {
                    n++;
                    string sub = Path.Combine(work, "sel" + n);
                    watch.Restart();

                    lastOut = Orbis.Run(exe,
                        "img_extract --passcode " + Zeros + " \"" + pkgPath + sel + "\" \"" + sub + "\"",
                        tools);

                    sfoPath = FindFile(work, "param.sfo");
                    Log.Write("选择性解包 " + sel + " 用时 " + watch.ElapsedMilliseconds + " ms，"
                        + (sfoPath == null ? "未拿到 param.sfo" : "成功"));

                    if (sfoPath != null)
                        break;
                }

                // 都不支持时退回整包（很慢，尽量别走到这里）
                if (sfoPath == null)
                {
                    Log.Write("选择性解包全部失败，退回整包解压（慢）");
                    watch.Restart();

                    lastOut = Orbis.Run(exe,
                        "img_extract --passcode " + Zeros + " \"" + pkgPath + "\" \"" + work + "\"",
                        tools);

                    Log.Write("整包解压用时 " + watch.ElapsedMilliseconds + " ms");

                    sfoPath = FindFile(work, "param.sfo");
                    if (sfoPath == null)
                    {
                        try { Directory.Delete(work, true); } catch { }
                        throw new Exception("解包后找不到 param.sfo。\r\n"
                            + "零售原包没有 passcode 解不开，只能导入 fpkg。\r\n\r\n"
                            + "orbis-pub-cmd 输出：\r\n" + Trim(lastOut));
                    }
                }
            }

            string dir = Path.GetDirectoryName(sfoPath);

            GameItem it = new GameItem();
            it.Source = "PKG";
            it.Origin = pkgPath;
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
