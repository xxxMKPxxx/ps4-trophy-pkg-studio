using System;

namespace PkgStudio
{
    /// <summary>
    /// 用 PS4_Tools 库直接读 pkg 元数据（和原版 Trophy Unlocker 同一套做法）。
    ///
    /// 它内部解析 pkg 头 + 条目表，只读需要的那几块，所以是秒级；
    /// 不像 orbis-pub-cmd 那样要整包解压。这里只取展示用的字段，
    /// 真正打包需要的 nptitle/npbind/trophy 字节仍然等到打包时再解（有缓存）。
    /// </summary>
    internal static class PkgFast
    {
        public static bool Fill(GameItem it)
        {
            try
            {
                PS4_Tools.PKG.SceneRelated.Unprotected_PKG pkg =
                    PS4_Tools.PKG.SceneRelated.Read_PKG(it.Origin);

                if (pkg == null)
                    return false;

                if (!string.IsNullOrEmpty(pkg.PS4_Title))
                    it.Title = pkg.PS4_Title;
                if (!string.IsNullOrEmpty(pkg.Content_ID))
                    it.ContentId = pkg.Content_ID;

                string tid = null, cat = null, ver = null;
                if (pkg.Param != null && pkg.Param.Tables != null)
                {
                    for (int i = 0; i < pkg.Param.Tables.Count; i++)
                    {
                        string name = pkg.Param.Tables[i].Name;
                        string value = pkg.Param.Tables[i].Value;

                        if (name == "TITLE_ID") tid = value;
                        else if (name == "CATEGORY") cat = value;
                        else if (name == "APP_VER") ver = value;
                    }
                }

                if (!string.IsNullOrEmpty(tid)) it.TitleId = tid;
                if (!string.IsNullOrEmpty(cat)) it.Category = cat;
                if (!string.IsNullOrEmpty(ver)) it.AppVer = ver;

                it.Kind = GameItem.KindOf(it.Category);
                return true;
            }
            catch (Exception ex)
            {
                Log.Write("PS4_Tools 读取失败（" + it.Origin + "）：" + ex.Message);
                return false;
            }
        }
    }
}
