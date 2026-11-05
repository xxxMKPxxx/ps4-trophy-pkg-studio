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

            // 就地读，不复制。包有多大都不动它，只在旁边解出 sce_sys 那几个小文件。
            string srcPkg = pkgPath;

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

                // sce_sys 里的文件（param.sfo / nptitle.dat / npbind.dat / trophy）是 pkg 条目，
                // 完整解包时它们落在 Sc0，Image0 才是 PFS 里的游戏本体。
                // 所以先试 Sc0，避免为了一个小文件去读整个 PFS。
                string[] fileSels = new string[]
                {
                    ":Sc0/sce_sys/param.sfo",
                    ":sce_sys/param.sfo",
                    ":Image0/sce_sys/param.sfo"
                };
                sfoPath = TrySelectors(exe, tools, srcPkg, work, fileSels, "file", watch);

                // 2) 取整个 sce_sys 目录
                if (sfoPath == null)
                {
                    string[] dirSels = new string[]
                    {
                        ":Sc0/sce_sys",
                        ":sce_sys",
                        ":Image0/sce_sys"
                    };
                    sfoPath = TrySelectors(exe, tools, srcPkg, work, dirSels, "dir", watch);
                }

                // 3) 最后退回整包（慢，尤其包在网络盘上）
                if (sfoPath == null)
                {
                    Log.Write("选择性解包不可用，退回整包解压（" + (fi.Length / 1024 / 1024) + " MB，会很慢）");
                    CheckSpace(work, fi.Length + 64L * 1024 * 1024);
                    watch.Restart();

                    string sub = Path.Combine(work, "full");
                    string outText = Orbis.Run(exe,
                        "img_extract --passcode " + Zeros + " \"" + srcPkg + "\" \"" + sub + "\"", tools);

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

            // 按文件名在全缓存里找，不假定几个文件都在同一目录（Sc0 / Image0 会分开）
            it.Nptitle = ReadFirst(work, "nptitle.dat");
            it.Npbind = ReadFirst(work, "npbind.dat");
            it.Trophy = ReadFirst(work, "trophy00.trp");

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

            // 就地读原件，不复制（包可能几十 GB）
            string srcPkg = it.Origin;

            if (!Directory.Exists(work))
                Directory.CreateDirectory(work);

            string lastExtractOutput = null;

            // 缺哪几个文件就从包里挑哪几个目录取，取到就停。绝不默认整包解压：
            // 一个 600MB 的包整包解压要等几十秒到几分钟，而 sce_sys 只有几 MB。
            if (!AllFilesPresent(work))
            {
                log("补齐 " + it.TitleId + " 的缺件…");

                // Sc0 = pkg 条目（sce_sys 里的文件都在这），Image0 = PFS 本体
                string[] dirSels = new string[]
                {
                    ":Sc0/sce_sys",
                    ":Image0/sce_sys",
                    ":sce_sys"
                };

                foreach (string sel in dirSels)
                {
                    if (AllFilesPresent(work))
                        break;

                    Stopwatch w = Stopwatch.StartNew();
                    string outText = RunExtract(exe, tools, srcPkg, work, sel, "need", w);

                    if (lastExtractOutput == null && FindFile(work, "param.sfo") == null)
                        lastExtractOutput = outText;
                }

                // 还缺 trophy 的话，单独按文件再试一次（有的包里只有它单独一个条目）
                if (ReadFirst(work, "trophy00.trp") == null)
                {
                    string[] trophySels = new string[]
                    {
                        ":Sc0/sce_sys/trophy/trophy00.trp",
                        ":Image0/sce_sys/trophy/trophy00.trp",
                        ":sce_sys/trophy/trophy00.trp"
                    };
                    Stopwatch w = Stopwatch.StartNew();
                    foreach (string sel in trophySels)
                    {
                        if (ReadFirst(work, "trophy00.trp") != null)
                            break;
                        RunExtract(exe, tools, srcPkg, work, sel, "trp", w);
                    }
                }

                // 前面全失败才真正整包解压（最慢，最后手段）
                if (!AllFilesPresent(work))
                {
                    Log.Write("逐个目录都没凑齐，退回整包解压: " + srcPkg);
                    long need = new FileInfo(srcPkg).Length;
                    CheckSpace(work, need + 64L * 1024 * 1024);

                    string sub = Path.Combine(work, "full");
                    Stopwatch w = Stopwatch.StartNew();
                    string outText = Orbis.Run(exe,
                        "img_extract --passcode " + Zeros + " \"" + srcPkg + "\" \"" + sub + "\"", tools);
                    Log.Write("整包解压用时 " + w.ElapsedMilliseconds + " ms，落地 " + CountFiles(sub));
                    lastExtractOutput = outText;
                }
            }

            string sfoPath = FindFile(work, "param.sfo");
            if (sfoPath == null)
            {
                throw new Exception("解包后仍然找不到 param.sfo。\r\n"
                    + "1) 这个包可能不是 fpkg（零售原包没有 passcode 解不开）\r\n"
                    + "2) 或者是 orbis-pub-cmd 打不开这个路径（网络盘 \\\\主机\\... 或路径里含中文）。\r\n"
                    + "   这种情况不用复制整个包，用 net use 映射一个盘符（如 Z:）再用 Z:\\ 打开即可。\r\n"
                    + "3) 或磁盘空间不足，当前缓存目录 " + work + "\r\n\r\n"
                    + "orbis-pub-cmd 输出：\r\n" + Trim(lastExtractOutput));
            }

            if (it.Param == null)
                it.Param = File.ReadAllBytes(sfoPath);

            it.Nptitle = ReadFirst(work, "nptitle.dat");
            it.Npbind = ReadFirst(work, "npbind.dat");
            it.Trophy = ReadFirst(work, "trophy00.trp");

            if (it.Npbind == null) throw new Exception("包里没有 npbind.dat");
            if (it.Nptitle == null) throw new Exception("包里没有 nptitle.dat");
            if (it.Trophy == null) throw new Exception("包里没有 trophy00.trp（sce_sys/trophy/ 下）");

            log("已补齐 " + it.TitleId);
        }

        /// <summary>param.sfo / nptitle / npbind / trophy 四个是否都已经在缓存里。</summary>
        private static bool AllFilesPresent(string work)
        {
            return FindFile(work, "param.sfo") != null
                && FindFile(work, "nptitle.dat") != null
                && FindFile(work, "npbind.dat") != null
                && FindFile(work, "trophy00.trp") != null;
        }

        private static byte[] ReadFirst(string root, string name)
        {
            string p = FindFile(root, name);
            return p != null ? File.ReadAllBytes(p) : null;
        }

        /// <summary>按 selector 选择性解出一部分内容，返回 orbis 的输出文本。</summary>
        private static string RunExtract(string exe, string tools, string pkgPath, string work,
                                         string selector, string tag, Stopwatch watch)
        {
            string sub = Path.Combine(work, tag + "_" + SafeName(selector));
            if (!Directory.Exists(sub))
                Directory.CreateDirectory(sub);

            watch.Restart();
            string outText;
            try
            {
                outText = Orbis.Run(exe,
                    "img_extract --passcode " + Zeros + " \"" + pkgPath + selector + "\" \"" + sub + "\"", tools);
            }
            catch (Exception ex)
            {
                Log.Write("选择性解包 " + selector + " 抛异常: " + ex.Message);
                return ex.Message;
            }

            int files = CountFiles(sub);
            Log.Write("选择性解包 " + selector + " 用时 " + watch.ElapsedMilliseconds + " ms，落地 "
                + files + " 个文件");

            if (files == 0)
            {
                string o = (outText ?? "").Trim();
                if (o.Length > 0)
                    Log.Write("  orbis 输出: " + (o.Length > 300 ? o.Substring(0, 300) : o));
                else
                    Log.Write("  orbis 没有任何输出，通常是它没能打开包（网络路径 / 中文路径 / 权限）");
            }

            return outText;
        }

        private static string SafeName(string selector)
        {
            char[] bad = new char[] { ':', '/', '\\', '*', '?', '"', '<', '>', '|' };
            string s = selector;
            foreach (char c in bad)
                s = s.Replace(c, '_');
            return s.Trim('_');
        }

        private static string TrySelectors(string exe, string tools, string pkgPath, string work,
                                          string[] selectors, string tag, Stopwatch watch)
        {
            foreach (string sel in selectors)
            {
                string sfo = FindFile(work, "param.sfo");
                if (sfo != null)
                    return sfo;

                RunExtract(exe, tools, pkgPath, work, sel, tag, watch);
            }
            return FindFile(work, "param.sfo");
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

        /// <summary>整包解压前先看空间，否则 orbis 只会默默失败。</summary>
        private static void CheckSpace(string dir, long needBytes)
        {
            string root;
            long free;

            try
            {
                root = Path.GetPathRoot(Path.GetFullPath(dir));
                if (string.IsNullOrEmpty(root))
                    return;

                free = new DriveInfo(root).AvailableFreeSpace;
            }
            catch (Exception ex)
            {
                // 拿不到磁盘信息不该拦住流程，记一笔就走
                Log.Write("读取磁盘空间失败，跳过检查: " + ex.Message);
                return;
            }

            Log.Write("缓存盘 " + root + " 可用 " + (free / 1024 / 1024) + " MB，需要约 "
                + (needBytes / 1024 / 1024) + " MB");

            if (free < needBytes)
            {
                throw new Exception("磁盘空间不足：整包解压需要约 "
                    + (needBytes / 1024 / 1024) + " MB，缓存盘 " + root + " 只剩 "
                    + (free / 1024 / 1024) + " MB。\r\n"
                    + "清理空间，或把系统临时目录改到大一点的盘再试。");
            }
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

    }
}
