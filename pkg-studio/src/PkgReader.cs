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

            string tmp = Path.Combine(workRoot, "import_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(tmp);

            string exe = Orbis.EnsureTool(templateDir, workRoot);
            string tools = Path.GetDirectoryName(exe);

            Log.Write("导入 pkg: " + pkgPath);
            string orbOut = Orbis.Run(exe,
                "img_extract --passcode 00000000000000000000000000000000 \"" + pkgPath + "\" \"" + tmp + "\"",
                tools);

            if (!string.IsNullOrEmpty(orbOut))
                Log.Write("img_extract 输出: " + orbOut.Trim());

            string sfoPath = FindFile(tmp, "param.sfo");
            if (sfoPath == null)
                throw new Exception("解包后找不到 param.sfo。\r\n"
                    + "零售原包没有 passcode 解不开，只能导入 fpkg。\r\n\r\n"
                    + "orbis-pub-cmd 输出：\r\n" + Trim(orbOut));

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

            Log.Write("导入结果: " + it.Title + " / " + it.TitleId + " / " + it.Category
                + " / nptitle=" + (it.Nptitle == null ? "无" : it.Nptitle.Length + "B")
                + " / npbind=" + (it.Npbind == null ? "无" : it.Npbind.Length + "B")
                + " / trophy=" + (it.Trophy == null ? "无" : it.Trophy.Length + "B"));

            try { Directory.Delete(tmp, true); } catch { }
            return it;
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
            if (!Directory.Exists(root))
                return null;

            string[] direct = Directory.GetFiles(root, name, SearchOption.AllDirectories);
            return direct.Length > 0 ? direct[0] : null;
        }

        public static string FindDir(string root, string name)
        {
            if (!Directory.Exists(root))
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

            // 有的包把奖杯放在 sce_sys/trophy/ 下一层
            string found = FindFile(dir, "trophy00.trp");
            return found != null ? File.ReadAllBytes(found) : null;
        }
    }
}
