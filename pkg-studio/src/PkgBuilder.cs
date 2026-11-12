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

                // 1. param.sfo：以模板自带的（Sony 已验证可打包的）SFO 作基底。
                //
                // 游戏 appmeta 里那份 param.sfo 是「当前安装版本」的：装过补丁的游戏
                // 会是 CATEGORY=gp / APP_VER=01.xx / TARGET_APP_VER / PUBTOOLINFO…
                // img_create 按 gp4 的 app_type="full" 校验，会直接报
                //   "Format of the param file is not valid. (sce_sys/param.sfo,
                //    unexpected Application Version (APP_VER))"
                // 模板 SFO 只有 20 个字段，APP_VER=01.00 / VERSION=01.00 / CATEGORY=gd，
                // 天然合法；CONTENT_ID 也是 EP 开头的本体格式，顺带避开 HP9000 补丁前缀。
                // 只做等长替换 + 写数据区，不重建 SFO 结构（整体重写曾导致 CE-34629）。
                string tplSfoPath = Path.Combine(target, "sce_sys", "param.sfo");
                if (!File.Exists(tplSfoPath))
                    throw new Exception("模板里缺 sce_sys/param.sfo，无法生成合法元数据");
                byte[] sfo = File.ReadAllBytes(tplSfoPath);

                string oldId = it.TitleId;
                if (string.IsNullOrEmpty(oldId) || oldId.Length != 9)
                    throw new Exception("原 TITLE_ID 不是 9 字符: " + oldId);

                string newId = NewTitleId(oldId);
                string tplId = Sfo.Get(sfo, "TITLE_ID");
                if (string.IsNullOrEmpty(tplId) || tplId.Length != 9)
                    throw new Exception("模板 param.sfo 的 TITLE_ID 异常: " + tplId);

                Sfo.ReplaceInPlace(sfo, "TITLE_ID", tplId, newId);
                Sfo.ReplaceInPlace(sfo, "CONTENT_ID", tplId, newId);
                string newContentId = Sfo.Get(sfo, "CONTENT_ID");
                Log.Write("param.sfo 用模板基底: TITLE_ID " + oldId + " -> " + newId
                    + " / CONTENT_ID: " + newContentId);
                log("TITLE_ID: " + oldId + " -> " + newId);
                log("CONTENT_ID: " + newContentId);

                // 显示名：从游戏 SFO 取原名，写进模板 TITLE（max 128，够用）。
                // 模板只有 TITLE 一个标题字段，直接带「奖杯：」前缀写入即可。
                string gameTitle = null;
                try { gameTitle = Sfo.Get(it.Param, "TITLE"); } catch { }
                if (string.IsNullOrEmpty(gameTitle)) gameTitle = it.Title;
                Sfo.SetString(sfo, "TITLE", "奖杯：" + gameTitle);
                string displayName = Sfo.Get(sfo, "TITLE");
                Log.Write("显示名称: " + displayName + "（模板 SFO 基底）");
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

                // 4.7b 奖杯集清单（合集类游戏的关键）。
                // npbind.dat 是受保护的系统文件：gp4 会把它打进 sce_sys/，系统 trophy
                // 服务能读，但 big app 运行时 open("/app0/sce_sys/npbind.dat") 会失败。
                // 所以打包时在这里把 label + NPCOMMID 解析成普通文本放 /app0 根目录，
                // 解锁器从 /app0/games.csv 读（和 font.ttf / trophy_order.csv 一样可读）。
                bool hasGames = useCustom;
                if (hasGames)
                {
                    int n = WriteGamesCsv(it.Npbind, Path.Combine(target, "games.csv"));
                    Log.Write("奖杯集清单: " + n + " 个（games.csv）");
                    log("奖杯集清单: " + n + " 个");
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

                if ((hasOrder || hasFont || hasGames) && xml.IndexOf("</files>", StringComparison.Ordinal) > 0)
                {
                    StringBuilder extra = new StringBuilder();

                    if (hasOrder)
                        extra.Append("    <file targ_path=\"trophy_order.csv\" orig_path=\"CONTENTS\\trophy_order.csv\" />\r\n");
                    if (hasFont)
                        extra.Append("    <file targ_path=\"font.ttf\" orig_path=\"CONTENTS\\font.ttf\" />\r\n");
                    if (hasGames)
                        extra.Append("    <file targ_path=\"games.csv\" orig_path=\"CONTENTS\\games.csv\" />\r\n");

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
        /// 把 npbind.dat 里的 service label + NPCOMMID 写成 app0 根目录可读的 games.csv。
        ///
        /// npbind = 132B 头 + N*384B 块 + 16B 尾；头 @0x1C 是大端块数，
        /// 块 +0 = "NPWRxxxxx_00"(12B)，+16 = ASCII 数字的 NP Service Label。
        /// 单奖杯集游戏是 '0'；Uncharted 合集是 '1'/'2'/'3'（没有 0）。
        /// CSV 每行：label,label_ascii,npcommid；'#' 开头是注释。
        /// 解析不出任何块时写一行 "0,48,single" 兜底，保证解锁器行为与旧版一致。
        /// </summary>
        private static int WriteGamesCsv(byte[] d, string path)
        {
            var lines = new List<string>();
            lines.Add("# label,label_ascii,npcommid");
            int n = 0;

            if (d != null && d.Length >= 148)
            {
                int blocks = (d[28] << 24) | (d[29] << 16) | (d[30] << 8) | d[31];
                if (blocks < 1 || blocks > 8) blocks = 1;

                for (int i = 0; i < blocks; i++)
                {
                    int off = 132 + i * 384;
                    if (off + 17 > d.Length) break;
                    if (d[off] != (byte)'N' || d[off + 1] != (byte)'P'
                        || d[off + 2] != (byte)'W' || d[off + 3] != (byte)'R')
                        continue;

                    byte c = d[off + 16];
                    int label = (c >= (byte)'0' && c <= (byte)'9') ? c - (byte)'0' : i;
                    string commid = Encoding.ASCII.GetString(d, off, 12).TrimEnd('\0');
                    lines.Add(label + "," + (int)c + "," + commid);
                    n++;
                }
            }

            if (n == 0)
            {
                lines.Add("0,48,single");
                n = 1;
            }

            File.WriteAllText(path, string.Join("\r\n", lines) + "\r\n", new UTF8Encoding(false));
            return n;
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

