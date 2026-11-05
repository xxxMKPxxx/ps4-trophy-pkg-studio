using System;

namespace PkgStudio
{
    internal enum GameKind
    {
        Game,
        Update,
        Dlc,
        Unknown
    }

    internal class GameItem
    {
        public string Source;        // "PKG" / "FTP"
        public string Origin;        // 文件路径或远端目录
        public string RootUrl;       // FTP 根，如 ftp://192.168.1.10:2121/
        public string PkgUrl;        // FTP 上的 app.pkg 地址（元数据缺失时直接从包里读）
        public string CacheDir;      // PKG 导入时解出来的目录（懒加载其余文件用）
        public string Title;
        public string TitleId;
        public string ContentId;
        public string AppVer;
        public string Category;

        public GameKind Kind = GameKind.Unknown;

        public byte[] Param;
        public byte[] Nptitle;
        public byte[] Npbind;
        public byte[] Trophy;

        public bool Usable
        {
            get { return Kind == GameKind.Game; }
        }

        public bool FilesLoaded
        {
            get { return Nptitle != null && Npbind != null && Trophy != null; }
        }

        public string KindText
        {
            get
            {
                switch (Kind)
                {
                    case GameKind.Game: return "游戏";
                    case GameKind.Update: return "更新";
                    case GameKind.Dlc: return "DLC";
                    default: return "未知";
                }
            }
        }

        public string StatusText
        {
            get
            {
                if (Kind == GameKind.Game) return "可打包";
                if (Kind == GameKind.Unknown) return "类别无法识别，不可用";
                return "不可用（" + KindText + "不能做奖杯包）";
            }
        }

        /// <summary>CATEGORY 是 param.sfo 里的两位代码：gd 本体、gp 更新、ac 附加内容。</summary>
        public static GameKind KindOf(string category)
        {
            if (string.IsNullOrEmpty(category))
                return GameKind.Unknown;

            switch (category.Trim().ToLowerInvariant())
            {
                case "gd": return GameKind.Game;
                case "gp": return GameKind.Update;
                case "ac": return GameKind.Dlc;
                case "gdd": return GameKind.Dlc;
                case "gdk": return GameKind.Dlc;
                case "gda": return GameKind.Dlc;
                default: return GameKind.Unknown;
            }
        }
    }
}
