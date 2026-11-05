using System;
using System.IO;

namespace PkgStudio
{
    /// <summary>
    /// 从 pkg 文件名里直接抠出 Content ID / Title ID。
    ///
    /// 官方命名格式：
    ///   HP0700-CUSA04986_00-PS4TOBERSERIA001-A0100-V0100.pkg
    ///   服务ID - TitleID_00 - 内容名 - 应用版本 - 内容版本
    ///
    /// 这样添加时不用碰文件内容，立刻就能出示 Title ID 和版本，
    /// 真正的解包放到后台做。
    /// </summary>
    internal static class PkgName
    {
        public static string TitleId(string filePath)
        {
            string[] p = Split(filePath);
            if (p.Length < 2)
                return null;

            string s = p[1];
            int us = s.IndexOf('_');
            if (us > 0)
                s = s.Substring(0, us);

            s = s.Trim().ToUpperInvariant();
            return s.Length == 9 ? s : null;
        }

        public static string ContentId(string filePath)
        {
            string[] p = Split(filePath);
            if (p.Length < 3)
                return null;
            return p[0] + "-" + p[1] + "-" + p[2];
        }

        /// <summary>A0100 -> 01.00</summary>
        public static string AppVer(string filePath)
        {
            string[] p = Split(filePath);
            if (p.Length < 4)
                return null;
            return Dotted(p[3]);
        }

        /// <summary>V0100 -> 01.00</summary>
        public static string Version(string filePath)
        {
            string[] p = Split(filePath);
            if (p.Length < 5)
                return null;
            return Dotted(p[4]);
        }

        /// <summary>内容名，作为解包完成前的临时标题</summary>
        public static string Label(string filePath)
        {
            string[] p = Split(filePath);
            if (p.Length >= 3 && p[2].Length > 0)
                return p[2];
            return Path.GetFileNameWithoutExtension(filePath);
        }

        private static string Dotted(string s)
        {
            s = s.Trim().ToUpperInvariant();
            if (s.Length < 5)
                return s;

            string digits = s.Substring(1);          // A0100 -> 0100
            if (digits.Length < 4)
                return digits;

            return digits.Substring(0, 2) + "." + digits.Substring(2, 2);
        }

        private static string[] Split(string filePath)
        {
            string name = Path.GetFileNameWithoutExtension(filePath);
            if (string.IsNullOrEmpty(name))
                return new string[0];
            return name.Split('-');
        }
    }
}
