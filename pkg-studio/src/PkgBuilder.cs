using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace PkgStudio
{
    internal static class PkgBuilder
    {
        /// <summary>
        /// 用模板里那套 Unlocker 外壳，把目标游戏的四个文件塞进去，打包成 pkg。
        /// 返回生成的 pkg 路径。
        /// </summary>
        public static string Build(GameItem it, string templateDir, string outDir, string workRoot,
                                   Action<string> log)
        {
            if (!it.Usable)
                throw new Exception("类型为「" + it.KindText + "」，不能做奖杯包");
            if (!it.FilesLoaded)
                throw new Exception("缺少 nptitle.dat / npbind.dat / trophy00.trp");

            string zip = Path.Combine(templateDir, "CONTENTS.zip");
            if (!File.Exists(zip))
                throw new Exception("模板缺失: " + zip);

            string gp4Src = FindGp4(templateDir);
            if (gp4Src == null)
                throw new Exception("模板目录里找不到 .gp4 工程文件");

            string orbis = Orbis.EnsureTool(templateDir, workRoot);
            string tools = Path.GetDirectoryName(orbis);

            string work = Path.Combine(workRoot, "build_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(work);

            try
            {
                log("解模板…");
                string tpl = Path.Combine(work, "tpl");
                ZipFile.ExtractToDirectory(zip, tpl);

                string contents = PkgReader.FindDir(tpl, "CONTENTS");
                string target = Path.Combine(work, "CONTENTS");
                if (contents == null)
                {
                    // zip 根目录本身就是 CONTENTS
                    Directory.Move(tpl, target);
                }
                else if (!string.Equals(contents, target, StringComparison.OrdinalIgnoreCase))
                {
                    Directory.Move(contents, target);
                }

                // 1. param.sfo：只做等长就地替换，不动结构
                byte[] sfo = (byte[])it.Param.Clone();
                string oldId = it.TitleId;
                if (string.IsNullOrEmpty(oldId) || oldId.Length != 9)
                    throw new Exception("原 TITLE_ID 不是 9 字符: " + oldId);

                string newId = NewTitleId(oldId);
                Sfo.ReplaceInPlace(sfo, "TITLE_ID", oldId, newId);
                try
                {
                    Sfo.ReplaceInPlace(sfo, "CONTENT_ID", oldId, newId);
                }
                catch (Exception ex)
                {
                    // CONTENT_ID 里不一定含完整的 9 字符，改不动就只记日志，不中断
                    Log.Write("警告：" + it.TitleId + " 的 CONTENT_ID 未改（" + ex.Message + "）");
                }
                string newContentId = Sfo.Get(sfo, "CONTENT_ID");
                Log.Write("TITLE_ID: " + oldId + " -> " + newId + " / CONTENT_ID: " + newContentId);
                log("TITLE_ID: " + oldId + " -> " + newId);
                log("CONTENT_ID: " + newContentId);

                string sceSys = Path.Combine(target, "sce_sys");
                Directory.CreateDirectory(sceSys);
                File.WriteAllBytes(Path.Combine(sceSys, "param.sfo"), sfo);

                // 2. nptitle.dat：把 12 字节的 "TITLEID_00" 换成新的
                byte[] npt = Sfo.ReplaceBytes(it.Nptitle,
                    Encoding.UTF8.GetBytes(oldId + "_00"),
                    Encoding.UTF8.GetBytes(newId + "_00"));
                File.WriteAllBytes(Path.Combine(sceSys, "nptitle.dat"), npt);

                // 3. npbind / trophy 原样拷贝
                File.WriteAllBytes(Path.Combine(sceSys, "npbind.dat"), it.Npbind);

                string trophyDir = Path.Combine(sceSys, "trophy");
                Directory.CreateDirectory(trophyDir);
                File.WriteAllBytes(Path.Combine(trophyDir, "trophy00.trp"), it.Trophy);

                // 4.5 自定义 eboot：template 目录下有 eboot.bin 就替换掉模板自带的
                //     （那是"启动即按顺序解锁"的独立 app，替换后 pkg 里跑的就是我们的逻辑）
                string ebootCustom = Path.Combine(templateDir, "eboot.bin");
                if (File.Exists(ebootCustom))
                {
                    File.Copy(ebootCustom, Path.Combine(target, "eboot.bin"), true);
                    log("已替换 eboot.bin（解锁器 app）");
                    Log.Write("使用自定义 eboot: " + ebootCustom);
                }

                // 4.6 解锁顺序表：输出目录里有 trophy_order_<TITLEID>.csv 就一起打进包里
                string orderLocal = Path.Combine(outDir, "trophy_order_" + it.TitleId + ".csv");
                bool hasOrder = File.Exists(orderLocal);
                if (hasOrder)
                {
                    File.Copy(orderLocal, Path.Combine(target, "trophy_order.csv"), true);
                    log("已加入解锁顺序表");
                    Log.Write("顺序表: " + orderLocal);
                }

                // 4. gp4：只改 content_id
                string xml = File.ReadAllText(gp4Src, Encoding.UTF8);
                xml = ReplaceContentId(xml, newContentId);

                if (hasOrder && xml.IndexOf("</files>", StringComparison.Ordinal) > 0)
                {
                    xml = xml.Replace("</files>",
                        "    <file targ_path=\"trophy_order.csv\" orig_path=\"CONTENTS\\trophy_order.csv\" />\r\n  </files>");
                }

                string gp4 = Path.Combine(work, "project.gp4");
                File.WriteAllText(gp4, xml, new UTF8Encoding(false));

                // 5. 打包
                if (!Directory.Exists(outDir))
                    Directory.CreateDirectory(outDir);

                log("打包中…");
                HashSet<string> before = new HashSet<string>(
                    Directory.GetFiles(outDir, "*.pkg"), StringComparer.OrdinalIgnoreCase);

                string output = Orbis.Run(orbis,
                    "img_create --oformat pkg \"" + gp4 + "\" \"" + outDir + "\"", tools);
                Log.Write("img_create 输出: " + (string.IsNullOrEmpty(output) ? "(空)" : Trim(output)));

                string made = null;
                foreach (string f in Directory.GetFiles(outDir, "*.pkg"))
                {
                    if (!before.Contains(f)) { made = f; break; }
                }
                if (made == null)
                    made = NewestPkg(outDir);
                if (made == null)
                    throw new Exception("orbis-pub-cmd 没有产出 pkg。输出：\n" + Trim(output));

                // 6. 改成易读的 ASCII 名字
                string nice = Path.Combine(outDir, SafeName(it.Title) + "_" + newId + ".pkg");
                try
                {
                    if (File.Exists(nice)) File.Delete(nice);
                    File.Move(made, nice);
                    made = nice;
                }
                catch { }

                return made;
            }
            finally
            {
                try { Directory.Delete(work, true); } catch { }
            }
        }

        private static string ReplaceContentId(string xml, string newContentId)
        {
            const string marker = "content_id=\"";
            int i = xml.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (i < 0)
                throw new Exception("gp4 里找不到 content_id");

            int start = i + marker.Length;
            int end = xml.IndexOf('"', start);
            if (end < 0)
                throw new Exception("gp4 的 content_id 格式异常");

            return xml.Substring(0, start) + newContentId + xml.Substring(end);
        }

        private static string FindGp4(string dir)
        {
            if (!Directory.Exists(dir))
                return null;
            string[] hits = Directory.GetFiles(dir, "*.gp4", SearchOption.TopDirectoryOnly);
            return hits.Length > 0 ? hits[0] : null;
        }

        private static string NewestPkg(string dir)
        {
            string best = null;
            DateTime bestTime = DateTime.MinValue;
            foreach (string f in Directory.GetFiles(dir, "*.pkg"))
            {
                DateTime t = File.GetLastWriteTimeUtc(f);
                if (t > bestTime) { bestTime = t; best = f; }
            }
            return best;
        }

        public static string SafeName(string title)
        {
            StringBuilder sb = new StringBuilder();
            if (!string.IsNullOrEmpty(title))
            {
                foreach (char c in title)
                {
                    if ((c >= '0' && c <= '9') || (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z')
                        || c == ' ' || c == '-' || c == '_')
                        sb.Append(c);
                    else if (char.IsWhiteSpace(c))
                        sb.Append(' ');
                }
            }
            string s = sb.ToString().Trim();
            while (s.Contains("  ")) s = s.Replace("  ", " ");
            if (s.Length > 60) s = s.Substring(0, 60).Trim();
            return s.Length == 0 ? "Unlocker" : s;
        }

        /// <summary>按原 Title ID 派生唯一的 XDPX#####，和旧版工具保持一致。</summary>
        public static string NewTitleId(string gameTitleId)
        {
            uint h = 2166136261u;
            foreach (char c in gameTitleId.ToUpperInvariant())
            {
                h ^= c;
                h *= 16777619u;
            }
            return "XDPX" + (h % 100000u).ToString("D5");
        }

        private static string Trim(string s)
        {
            if (string.IsNullOrEmpty(s)) return "(无输出)";
            return s.Length > 1500 ? s.Substring(0, 1500) : s;
        }
    }
}
