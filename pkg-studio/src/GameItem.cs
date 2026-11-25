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
        public byte[] Trophy;        // 主 TRP（取 trophy00，没有则取编号最小的）
        public System.Collections.Generic.List<byte[]> Trophies;  // 包里全部 trophyNN.trp（合集可能多个）
        public byte[] Icon0;         // 游戏自己的 icon0.png，用作解锁器图标的底图

        public bool Usable
        {
            get { return Kind == GameKind.Game; }
        }

        /// <summary>
        /// 打包必需的两个文件。TRP 是可选的：多 commid / 多奖杯组的游戏本来就走
        /// no-trophy-trp（靠主机 /user/trophy/conf/ 里已有的定义），没有 TRP 也能出包。
        /// 以前把 Trophy 也算进来，导致 trophy 不从 00 开始（只有 trophy01/02）的包
        /// 被判成缺件、已读到的 param/nptitle/npbind 被整个丢掉，再转去走打不开的 orbis。
        /// </summary>
        public bool FilesLoaded
        {
            get { return Nptitle != null && Npbind != null; }
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
