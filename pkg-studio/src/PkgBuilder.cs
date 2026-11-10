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
        /// 
        /// 注意 eboot.bin 必须是 template 目录里那份 SCE ELF（create-fself 的 -out 产物），
        /// 普通 ELF / 手工改 e_type 的假 SCE ELF 装进主机都会报
        /// CE-30045-2 或 CE-41839-5（Cannot start the application）。
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

                // 装过补丁的游戏，appmeta 里的 param.sfo 是补丁版（CATEGORY=gp，
                // APP_VER 变成补丁版本、CONTENT_ID 是 HP9000 前缀）。目录仍是游戏本体，
                // nptitle/npbind 也仍指向原游戏奖杯集，但解锁器是独立应用，
                // CATEGORY 必须是 gd，留着 gp 会被系统当成补丁包。
                // 等长就地替换，不重建 SFO 结构 —— 整体重写曾导致 PS4 拒装（CE-34629）。
                string cat = Sfo.Get(sfo, "CATEGORY");
                if (string.Equals(cat, "gp", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        Sfo.ReplaceInPlace(sfo, "CATEGORY", "gp", "gd");
                        Log.Write("CATEGORY: gp -> gd（" + it.TitleId + " 已打补丁，按本体出包）");
                        log("CATEGORY: gp -> gd");
                    }
                    catch (Exception ex)
                    {
                        // 少数 SFO 没有 CATEGORY 或值不标准：不中断打包，但留下告警
                        Log.Write("警告：CATEGORY 未能改为 gd（" + ex.Message + "）");
                    }
                }

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

                // 1.5 显示名称加「奖杯：」前缀，PS4 主界面上和原游戏区分开。
                //     所有语言的 TITLE / STITLE 都改（中文系统读的是对应语言字段），
                //     放不下的按 UTF-8 字符边界裁剪，不破坏 SFO 结构。
                int names = Sfo.PrefixTitles(sfo, "奖杯：");
                string displayName = Sfo.Get(sfo, "TITLE");
                Log.Write("显示名称: " + displayName + "（改了 " + names + " 个字段）");
                log("显示名称: " + displayName);

                string sceSys = Path.Combine(target, "sce_sys");
                Directory.CreateDirectory(sceSys);
                File.WriteAllBytes(Path.Combine(sceSys, "param.sfo"), sfo);

                // 2. nptitle.dat：NP Title ID 必须和 param.sfo 的 TITLE_ID 一致，
                //    否则 img_create 直接拒绝打包：
                //    "Format of the NP Title file is not valid. (nptitle.dat, NP Title ID mismatch)"
                //    所以这里跟着改成新 ID（"TITLEID_00" → "XDPXxxxxx_00"）。
                byte[] npt = Sfo.ReplaceBytes(it.Nptitle,
                    Encoding.UTF8.GetBytes(oldId + "_00"),
                    Encoding.UTF8.GetBytes(newId + "_00"));
                File.WriteAllBytes(Path.Combine(sceSys, "nptitle.dat"), npt);
                File.WriteAllBytes(Path.Combine(sceSys, "npbind.dat"), it.Npbind);

                // 3. trophy00.trp：按「DLC 奖杯组数」决定要不要打进包。
                //    组数 = TRP 条目表里 GRxxx.PNG 图标的数量（已用主机上 5 个真实游戏验证，
                //    和奖杯数据库 group_num 完全一致；注意 TROP_XX.ESFM 文件数≠组数，不能用它判断）。
                //
                //    单组游戏（GR=0，无 DLC 奖杯）：打包 TRP，已长期验证正常。
                //    多组游戏（GR>0，带 DLC 奖杯，如情热传说 GR=1、DRIVECLUB GR=21）：
                //      自动【不】打包 TRP。主机上的奖杯定义以游戏/DLC 更新出来的为准，
                //      解锁器靠 npbind 的 NPCOMMID 注册到已存在的奖杯集即可。
                //      前提：这台主机上该游戏启动过、奖杯集已生成；没启动过会注册失败，
                //      先进一次游戏再装解锁包。
                //      背景：多组游戏带 TRP 出包曾有翻车记录（情热传说事件）——奖杯集注册
                //      中途失败会在主机 trophy_local.db 里留下「空记录」（title 行在、明细行
                //      全空），导致全主机任何奖杯操作都弹 NP-32339-0，且无法自愈。
                //
                //    手动开关（template 下放空文件）：
                //      no-trophy-trp    无论如何都不打包 TRP
                //      force-trophy-trp 强制打包 TRP（对照实验用，危险）
                int grps = CountTrophyGroupIcons(it.Trophy);
                bool markerNoTrp = File.Exists(Path.Combine(templateDir, "no-trophy-trp"));
                bool markerForceTrp = File.Exists(Path.Combine(templateDir, "force-trophy-trp"));
                bool skipTrp;
                if (markerNoTrp)
                {
                    skipTrp = true;
                    log("no-trophy-trp 标记：不打包 trophy00.trp");
                    Log.Write("no-trophy-trp: 手动标记生效，不打包 TRP");
                }
                else if (grps > 0 && !markerForceTrp)
                {
                    skipTrp = true;
                    log("检测到 " + grps + " 个 DLC 奖杯组（多组奖杯集）→ 自动不打包 trophy00.trp");
                    log("解锁器将注册到主机上已有的奖杯集（需该游戏在本机启动过一次）");
                    Log.Write("多组保护：TRP 含 " + grps + " 个 DLC 奖杯组（GRxxx.PNG），自动 no-trophy-trp。"
                        + "多组游戏带 TRP 出包可能导致主机奖杯库出现空记录（NP-32339-0 全局报错，情热传说事件）。"
                        + "如需强制打包，template 放 force-trophy-trp 标记。");
                }
                else
                {
                    skipTrp = false;
                    if (markerForceTrp && grps > 0)
                    {
                        log("警告：force-trophy-trp 强制打包 TRP（" + grps + " 个 DLC 组，有风险）");
                        Log.Write("警告：force-trophy-trp 标记生效，强制打包多组 TRP（" + grps + " 组）");
                    }
                    else if (grps < 0)
                    {
                        log("无法解析 TRP 结构，按原样打包 trophy00.trp");
                        Log.Write("TRP 解析失败（magic 或条目表异常），按原样打包");
                    }
                    else
                    {
                        log("单组奖杯集（无 DLC 组），正常打包 trophy00.trp");
                        Log.Write("TRP 组数=0（单组），正常打包 TRP");
                    }
                    string trophyDir = Path.Combine(sceSys, "trophy");
                    Directory.CreateDirectory(trophyDir);
                    File.WriteAllBytes(Path.Combine(trophyDir, "trophy00.trp"), it.Trophy);
                }

                // 4.5 eboot：默认用自研 UI 版（unlocker-app，CI 放在 template/eboot.bin）。
                //     796d596 这版在「不带 DLC 奖杯」的游戏上已验证一切正常。
                //     曾怀疑它导致 NP 错误而默认换回老工具 eboot，实测证伪：
                //     带 DLC 奖杯的游戏用老工具 eboot 一样报错，分界线是 DLC 奖杯组。
                //     做对照实验时，在 template 目录放一个空的 use-legacy-eboot 标记，
                //     就会改用 CONTENTS.zip 自带的 8.6MB 官方 np_trophy sample eboot。
                string ebootCustom = Path.Combine(templateDir, "eboot.bin");
                bool useLegacy = File.Exists(Path.Combine(templateDir, "use-legacy-eboot"));
                bool useCustom = !useLegacy && File.Exists(ebootCustom);

                if (useCustom)
                {
                    // 只认 create-fself 产出的 SCE ELF（e_type=0xFE10）。
                    // 普通 ELF / SELF 打进去会直接 CE-30045-2，与其装完才失败不如这里就说清楚。
                    byte[] eh = File.ReadAllBytes(ebootCustom);
                    if (eh.Length < 20 || eh[0] != 0x7F || eh[1] != (byte)'E' ||
                        eh[2] != (byte)'L' || eh[3] != (byte)'F' ||
                        (eh[16] | (eh[17] << 8)) != 0xFE10)
                    {
                        log("警告：eboot.bin 不是 SCE ELF，生成的包可能无法启动");
                        Log.Write("警告：template/eboot.bin 不是 SCE ELF（e_type 应为 0xFE10）");
                    }

                    string hash;
                    using (var sha = System.Security.Cryptography.SHA1.Create())
                        hash = BitConverter.ToString(sha.ComputeHash(eh)).Replace("-", "").Substring(0, 8);

                    File.Copy(ebootCustom, Path.Combine(target, "eboot.bin"), true);
                    log("已替换 eboot.bin（自研 UI 版，" + eh.Length + " 字节 SCE ELF，sha1 " + hash + "）");
                    Log.Write("使用自研 eboot: " + ebootCustom + "（" + eh.Length + " 字节, sha1 " + hash + "）");
                }
                else
                {
                    string ebootLegacy = Path.Combine(target, "eboot.bin");
                    byte[] eh = File.Exists(ebootLegacy) ? File.ReadAllBytes(ebootLegacy) : new byte[0];
                    bool sce = eh.Length >= 20 && eh[0] == 0x7F && eh[1] == (byte)'E'
                               && eh[2] == (byte)'L' && eh[3] == (byte)'F'
                               && (eh[16] | (eh[17] << 8)) == 0xFE10;
                    string hash;
                    using (var sha = System.Security.Cryptography.SHA1.Create())
                        hash = BitConverter.ToString(sha.ComputeHash(eh)).Replace("-", "").Substring(0, 8);

                    log("[对照] 使用模板自带的老工具 eboot（" + eh.Length + " 字节"
                        + (sce ? " SCE ELF" : "，格式异常") + "，sha1 " + hash + "）");
                    Log.Write("eboot: 保留 CONTENTS.zip 自带（老工具官方 np_trophy sample），"
                        + eh.Length + " 字节, sha1 " + hash + ", SCE=" + sce);
                    if (!sce)
                        throw new Exception("模板自带的 eboot.bin 不是 SCE ELF，无法打包");
                }

                // 4.6 解锁顺序表：只有自研 UI 版读 /app0/trophy_order.csv，老工具 eboot 不读
                string orderLocal = Path.Combine(outDir, "trophy_order_" + it.TitleId + ".csv");
                bool hasOrder = useCustom && File.Exists(orderLocal);
                if (hasOrder)
                {
                    File.Copy(orderLocal, Path.Combine(target, "trophy_order.csv"), true);
                    log("已加入解锁顺序表（延迟单位：秒）");
                    Log.Write("顺序表: " + orderLocal);
                }

                // 4.7 界面字体（只有自研 UI 版用 FreeType 画中文，读 /app0/font.ttf）
                string fontCustom = Path.Combine(templateDir, "font.ttf");
                bool hasFont = useCustom && File.Exists(fontCustom);
                if (hasFont)
                {
                    File.Copy(fontCustom, Path.Combine(target, "font.ttf"), true);
                    Log.Write("界面字体: " + fontCustom);
                }
                else if (useCustom)
                {
                    Log.Write("警告：template 里没有 font.ttf，解锁器界面会因为没有字体而退出");
                    log("警告：template 缺 font.ttf，界面起不来，请用最新版工具目录");
                }

                // 4.8 图标：用游戏自己的 icon0.png 当底图，中间叠一个奖杯图标。
                //     gp4 里本来就有 sce_sys/icon0.png 条目，直接覆盖文件即可。
                string overlayPath = Path.Combine(templateDir, "icon1.png");
                byte[] overlay = File.Exists(overlayPath) ? File.ReadAllBytes(overlayPath) : null;
                string iconDst = Path.Combine(sceSys, "icon0.png");
                byte[] composed = IconBuilder.Compose(it.Icon0, overlay, iconDst);
                if (composed != null)
                {
                    File.WriteAllBytes(iconDst, composed);
                    log("图标：游戏封面 + 奖杯（512×512）");
                    Log.Write("已合成 icon0.png（" + composed.Length + "B，底图 "
                        + (it.Icon0 != null && it.Icon0.Length > 0 ? "游戏图标" : "模板图标") + "）");
                }
                else if (overlay == null)
                {
                    log("警告：template 里缺 icon1.png，图标保持模板原样");
                    Log.Write("警告：template 里缺 icon1.png，未合成图标");
                }
                else
                {
                    log("警告：没有可用底图，图标保持模板原样");
                }

                // 4. gp4：只改 content_id
                string xml = File.ReadAllText(gp4Src, Encoding.UTF8);
                xml = ReplaceContentId(xml, newContentId);

                // 4.9 不打包 TRP 时，必须把 gp4 里对应的文件条目一并移除，
                //     否则 img_create 会因为 orig 文件缺失直接报错。
                if (skipTrp)
                {
                    const string trpLine = "<file targ_path=\"sce_sys/trophy/trophy00.trp\"";
                    int p = xml.IndexOf(trpLine, StringComparison.OrdinalIgnoreCase);
                    if (p < 0)
                        throw new Exception("已决定不打包 trophy00.trp，但 gp4 里没找到对应文件条目，无法继续");
                    int e = xml.IndexOf('>', p);
                    if (e < 0)
                        throw new Exception("gp4 里 trophy00.trp 条目格式异常");
                    xml = xml.Remove(p, e - p + 1);
                    log("gp4 已移除 trophy00.trp 条目");
                    Log.Write("no-trophy-trp: 已从 gp4 移除 sce_sys/trophy/trophy00.trp");
                }

                if ((hasOrder || hasFont) && xml.IndexOf("</files>", StringComparison.Ordinal) > 0)
                {
                    StringBuilder extra = new StringBuilder();

                    if (hasOrder)
                        extra.Append("    <file targ_path=\"trophy_order.csv\" orig_path=\"CONTENTS\\trophy_order.csv\" />\r\n");
                    if (hasFont)
                        extra.Append("    <file targ_path=\"font.ttf\" orig_path=\"CONTENTS\\font.ttf\" />\r\n");

                    xml = xml.Replace("</files>", extra.ToString() + "  </files>");
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

                if (output.IndexOf("[Error]", StringComparison.OrdinalIgnoreCase) >= 0)
                    throw new Exception("img_create 报错了，产物不可用。\r\n\r\norbis-pub-cmd 输出：\r\n" + Trim(output));

                string made = null;
                foreach (string f in Directory.GetFiles(outDir, "*.pkg"))
                {
                    if (!before.Contains(f)) { made = f; break; }
                }
                if (made == null)
                    throw new Exception("img_create 没有产出 pkg（输出目录里没有新文件）。\r\n\r\norbis-pub-cmd 输出：\r\n" + Trim(output));

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

        /// <summary>
        /// 数 TRP 里 DLC 奖杯组图标（GRxxx.PNG）的数量 = DLC 奖杯组数。
        ///
        /// TRP 条目表结构已用主机上 5 个真实游戏的 conf 文件验证：
        /// 文件头 magic 为 DCA24D00（落盘字节 dc a2 4d 00），条目表从 0x60 开始，
        /// 每条 0x40 字节，前 32 字节是以 \0 结尾的文件名。
        /// 实测组图标数与主机 trophy_local.db 的 group_num 完全一致：
        ///   情热传说 1（艾莉莎後日譚） / NPWR06616 0 / NPWR09367 1 /
        ///   DRIVECLUB 21 / DJMAX RESPECT 14
        /// 注意：TROP_XX.ESFM 文件数≠组数（单组游戏也常有十几个 ESFM），不能用它判断。
        /// 解析失败（magic 不对 / 条目表读不下去）返回 -1，调用方按原样打包兜底。
        /// </summary>
        private static int CountTrophyGroupIcons(byte[] trp)
        {
            if (trp == null || trp.Length < 0xA0)
                return -1;
            if (trp[0] != 0xDC || trp[1] != 0xA2 || trp[2] != 0x4D || trp[3] != 0x00)
                return -1;

            int count = 0;
            for (int off = 0x60; off + 0x40 <= trp.Length; off += 0x40)
            {
                int len = 0;
                while (len < 32 && trp[off + len] != 0) len++;
                if (len == 0) break;                        // 空名 = 条目表结束
                bool printable = true;
                for (int i = 0; i < len; i++)
                {
                    byte c = trp[off + i];
                    if (c < 0x21 || c > 0x7E) { printable = false; break; }
                }
                if (!printable) break;                      // 走进数据区了

                // GR + 至少 1 位数字 + .PNG
                if (len > 6 && trp[off] == (byte)'G' && trp[off + 1] == (byte)'R'
                    && trp[off + len - 4] == (byte)'.' && trp[off + len - 3] == (byte)'P'
                    && trp[off + len - 2] == (byte)'N' && trp[off + len - 1] == (byte)'G')
                {
                    bool digits = true;
                    for (int i = 2; i < len - 4; i++)
                    {
                        byte c = trp[off + i];
                        if (c < '0' || c > '9') { digits = false; break; }
                    }
                    if (digits) count++;
                }
            }
            return count;
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

