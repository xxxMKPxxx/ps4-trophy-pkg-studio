using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace PkgStudio
{
    /// <summary>
    /// 直接用 PS4_Tools 库读 pkg —— 和原版 TrophyUnlocker 完全一样的做法。
    ///
    /// 关键点：Read_PKG 返回的对象里已经带着**全部条目数据**（pkg.Entires[i].file_data），
    /// 原版就是靠遍历条目、按 CustomName 取需要的四个文件，全程不解包、不调用 orbis-pub-cmd。
    /// 我一开始以为它只能读元数据，白绕了一大圈去解包，这里改回原版的做法。
    /// </summary>
    internal static class PkgFast
    {
        // 条目名常量，与原版 Form1.btnPKG_Click 里逐字一致（注意 TROPHY 是两个下划线）
        private const string NAME_PARAM = "PARAM_SFO";
        private const string NAME_NPTITLE = "NPTITLE_DAT";
        private const string NAME_NPBIND = "NPBIND_DAT";
        private const string NAME_ICON0 = "ICON0_PNG";

        // trophy 条目名不是永远是 trophy00：实测 PROTOTYPE BIOHAZARD（CUSA06545）
        // 只有 TROPHY__TROPHY01_TRP / TROPHY__TROPHY02_TRP，鬼泣合集、Uncharted Legacy
        // 也带多个。写死 00 会让这些包 trophy=无，进而被判成缺件走 orbis（而 orbis 往往打不开）。
        // 按编号匹配任意一个，全部收集（合集用），主 TRP 取编号最小的。
        private static readonly Regex ReTrophy = new Regex(@"^TROPHY__TROPHY(\d+)_TRP$", RegexOptions.IgnoreCase);

        public static bool Fill(GameItem it)
        {
            try
            {
                PS4_Tools.PKG.SceneRelated.Unprotected_PKG pkg =
                    PS4_Tools.PKG.SceneRelated.Read_PKG(it.Origin);

                if (pkg == null)
                    return false;

                // ---- 1. 直接从条目表里取四个文件，不解包 ----
                List<KeyValuePair<int, byte[]>> trophies = new List<KeyValuePair<int, byte[]>>();
                if (pkg.Entires != null)
                {
                    for (int i = 0; i < pkg.Entires.Length; i++)
                    {
                        var entry = pkg.Entires[i];
                        if (entry.file_data == null)
                            continue;

                        string name = entry.CustomName ?? "";
                        switch (name)
                        {
                            case NAME_PARAM: it.Param = entry.file_data; break;
                            case NAME_NPTITLE: it.Nptitle = entry.file_data; break;
                            case NAME_NPBIND: it.Npbind = entry.file_data; break;
                            case NAME_ICON0: it.Icon0 = entry.file_data; break;
                            default:
                                Match m = ReTrophy.Match(name);
                                if (m.Success)
                                {
                                    int num;
                                    if (!int.TryParse(m.Groups[1].Value, out num)) num = int.MaxValue;
                                    trophies.Add(new KeyValuePair<int, byte[]>(num, entry.file_data));
                                }
                                break;
                        }
                    }
                }

                // 主 TRP 取编号最小的；全部保留给打包器按需使用
                if (trophies.Count > 0)
                {
                    trophies.Sort(delegate (KeyValuePair<int, byte[]> a, KeyValuePair<int, byte[]> b)
                    {
                        return a.Key.CompareTo(b.Key);
                    });
                    it.Trophies = new List<byte[]>();
                    foreach (KeyValuePair<int, byte[]> kv in trophies)
                        it.Trophies.Add(kv.Value);
                    it.Trophy = trophies[0].Value;
                }

                // ---- 2. 元数据：从 param.sfo 字节里解，用自己的解析器，不依赖库的内部结构 ----
                if (it.Param != null)
                {
                    string title = Sfo.Get(it.Param, "TITLE");
                    string tid = Sfo.Get(it.Param, "TITLE_ID");
                    string cat = Sfo.Get(it.Param, "CATEGORY");
                    string ver = Sfo.Get(it.Param, "APP_VER");

                    if (!string.IsNullOrEmpty(title)) it.Title = title;
                    if (!string.IsNullOrEmpty(tid)) it.TitleId = tid;
                    if (!string.IsNullOrEmpty(cat)) it.Category = cat;
                    if (!string.IsNullOrEmpty(ver)) it.AppVer = ver;
                }

                // 兜底：库给的标题 / Content ID
                if (string.IsNullOrEmpty(it.Title) && !string.IsNullOrEmpty(pkg.PS4_Title))
                    it.Title = pkg.PS4_Title;
                if (string.IsNullOrEmpty(it.ContentId) && !string.IsNullOrEmpty(pkg.Content_ID))
                    it.ContentId = pkg.Content_ID;

                it.Kind = GameItem.KindOf(it.Category);

                Log.Write("pkg 直读: " + it.Title + " / " + it.TitleId + " / " + it.Category
                    + " | param=" + Size(it.Param)
                    + " nptitle=" + Size(it.Nptitle)
                    + " npbind=" + Size(it.Npbind)
                    + " trophy=" + Size(it.Trophy)
                    + " (共 " + (it.Trophies == null ? 0 : it.Trophies.Count) + " 个 TRP)");

                return true;
            }
            catch (Exception ex)
            {
                Log.Write("PS4_Tools 读取失败（" + it.Origin + "）：" + ex.Message);
                return false;
            }
        }

        private static string Size(byte[] b)
        {
            return b == null ? "无" : (b.Length / 1024) + "KB";
        }
    }
}
