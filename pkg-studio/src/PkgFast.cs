using System;

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
        private const string NAME_TROPHY = "TROPHY__TROPHY00_TRP";

        public static bool Fill(GameItem it)
        {
            try
            {
                PS4_Tools.PKG.SceneRelated.Unprotected_PKG pkg =
                    PS4_Tools.PKG.SceneRelated.Read_PKG(it.Origin);

                if (pkg == null)
                    return false;

                // ---- 1. 直接从条目表里取四个文件，不解包 ----
                if (pkg.Entires != null)
                {
                    for (int i = 0; i < pkg.Entires.Length; i++)
                    {
                        var entry = pkg.Entires[i];
                        if (entry.file_data == null)
                            continue;

                        switch (entry.CustomName)
                        {
                            case NAME_PARAM: it.Param = entry.file_data; break;
                            case NAME_NPTITLE: it.Nptitle = entry.file_data; break;
                            case NAME_NPBIND: it.Npbind = entry.file_data; break;
                            case NAME_TROPHY: it.Trophy = entry.file_data; break;
                        }
                    }
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
                    + " trophy=" + Size(it.Trophy));

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
