using System;
using System.Collections.Generic;
using System.Text;

namespace PkgStudio
{
    /// <summary>
    /// param.sfo 读取与"等长就地替换"。
    ///
    /// 只做等长替换，不重建任何表结构 —— 之前尝试整体重写 param.sfo，
    /// 结果 PS4 直接拒绝安装（CE-34629）。TITLE_ID 是 9 字符、CONTENT_ID
    /// 里嵌的也是这 9 字符，长度天然一致，改成同长度就可能原地改写，
    /// 文件大小、偏移、对齐全都不变。
    /// </summary>
    internal static class Sfo
    {
        private const uint Magic = 0x46535000;

        internal struct Field
        {
            public string Key;
            public int DataOffset;
            public int Len;
        }

        public static List<Field> Parse(byte[] buf)
        {
            if (buf == null || buf.Length < 20)
                throw new Exception("SFO 太小");

            if (BitConverter.ToUInt32(buf, 0) != Magic)
                throw new Exception("不是 param.sfo（magic 不对）");

            uint keyStart = BitConverter.ToUInt32(buf, 8);
            uint dataStart = BitConverter.ToUInt32(buf, 12);
            uint count = BitConverter.ToUInt32(buf, 16);

            if (count > 512 || keyStart >= buf.Length || dataStart > buf.Length)
                throw new Exception("SFO 头部异常");

            List<Field> list = new List<Field>();
            for (int i = 0; i < count; i++)
            {
                int e = 20 + i * 16;
                if (e + 16 > buf.Length)
                    throw new Exception("SFO 条目越界");

                ushort ko = BitConverter.ToUInt16(buf, e);
                uint len = BitConverter.ToUInt32(buf, e + 4);
                uint dOff = BitConverter.ToUInt32(buf, e + 12);

                int kp = (int)(keyStart + ko);
                if (kp < 0 || kp >= buf.Length)
                    throw new Exception("SFO key 越界");

                int ke = kp;
                while (ke < buf.Length && buf[ke] != 0)
                    ke++;

                long dp = (long)dataStart + dOff;
                if (dp + len > buf.Length)
                    throw new Exception("SFO 值越界");

                Field f = new Field();
                f.Key = Encoding.ASCII.GetString(buf, kp, ke - kp);
                f.DataOffset = (int)dp;
                f.Len = (int)len;
                list.Add(f);
            }
            return list;
        }

        public static string Get(byte[] buf, string key)
        {
            foreach (Field f in Parse(buf))
            {
                if (f.Key != key)
                    continue;

                int n = f.Len;
                while (n > 0 && buf[f.DataOffset + n - 1] == 0)
                    n--;
                return Encoding.UTF8.GetString(buf, f.DataOffset, n);
            }
            return null;
        }

        /// <summary>等长就地替换字段值里的一段文本，不动结构、不改文件大小。</summary>
        public static void ReplaceInPlace(byte[] buf, string key, string oldText, string newText)
        {
            if (oldText == null || newText == null || oldText.Length != newText.Length)
                throw new Exception("等长替换要求新旧文本长度一致");

            foreach (Field f in Parse(buf))
            {
                if (f.Key != key)
                    continue;

                string cur = Encoding.UTF8.GetString(buf, f.DataOffset, f.Len);
                int idx = cur.IndexOf(oldText, StringComparison.Ordinal);
                if (idx < 0)
                    throw new Exception("字段 " + key + " 里找不到 " + oldText);

                byte[] nb = Encoding.UTF8.GetBytes(newText);
                if (f.DataOffset + idx + nb.Length > buf.Length)
                    throw new Exception("替换越界");

                Buffer.BlockCopy(nb, 0, buf, f.DataOffset + idx, nb.Length);
                return;
            }
            throw new Exception("找不到字段 " + key);
        }

        /// <summary>二进制里按字节串等长替换（nptitle.dat 用）。</summary>
        public static byte[] ReplaceBytes(byte[] data, byte[] oldBytes, byte[] newBytes)
        {
            if (oldBytes.Length != newBytes.Length)
                throw new Exception("nptitle 替换要求长度一致");

            for (int i = 0; i + oldBytes.Length <= data.Length; i++)
            {
                bool hit = true;
                for (int j = 0; j < oldBytes.Length; j++)
                {
                    if (data[i + j] != oldBytes[j]) { hit = false; break; }
                }
                if (!hit)
                    continue;

                byte[] outBuf = (byte[])data.Clone();
                Buffer.BlockCopy(newBytes, 0, outBuf, i, newBytes.Length);
                return outBuf;
            }
            throw new Exception("nptitle.dat 里找不到原 Title ID");
        }
    }
}
