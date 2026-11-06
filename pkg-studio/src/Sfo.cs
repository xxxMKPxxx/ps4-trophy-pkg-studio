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

        /// <summary>
        /// 给所有显示名称字段（TITLE / STITLE / 各语言的 TITLE_xx）就地加前缀。
        ///
        /// 只写数据区 + 更新 used_len，**不动 key 表、不重建结构、不改文件大小**：
        /// 之前整体重写 param.sfo 会导致 PS4 拒绝安装（CE-34629）。
        /// 放不下时按 UTF-8 字符边界裁剪原名称，保证不会写出半个汉字。
        /// </summary>
        public static int PrefixTitles(byte[] buf, string prefix)
        {
            if (buf == null || buf.Length < 20 || string.IsNullOrEmpty(prefix))
                return 0;

            uint keyStart = BitConverter.ToUInt32(buf, 8);
            uint dataStart = BitConverter.ToUInt32(buf, 12);
            uint count = BitConverter.ToUInt32(buf, 16);
            if (count > 512)
                return 0;

            int changed = 0;
            for (int i = 0; i < count; i++)
            {
                int e = 20 + i * 16;
                if (e + 16 > buf.Length)
                    break;

                ushort fmt = BitConverter.ToUInt16(buf, e + 2);
                if (fmt != 0x0204)                 // 只动 UTF-8 字符串字段
                    continue;

                ushort ko = BitConverter.ToUInt16(buf, e);
                uint used = BitConverter.ToUInt32(buf, e + 4);
                uint max = BitConverter.ToUInt32(buf, e + 8);
                uint dOff = BitConverter.ToUInt32(buf, e + 12);

                int kp = (int)(keyStart + ko);
                long dp = (long)dataStart + dOff;
                if (kp < 0 || kp >= buf.Length || dp < 0 || dp + max > buf.Length || max == 0)
                    continue;

                int ke = kp;
                while (ke < buf.Length && buf[ke] != 0)
                    ke++;
                string key = Encoding.ASCII.GetString(buf, kp, ke - kp);
                if (!IsTitleKey(key))
                    continue;

                int n = (int)Math.Min(used, max);
                while (n > 0 && buf[(int)dp + n - 1] == 0)
                    n--;
                string cur = n > 0 ? Encoding.UTF8.GetString(buf, (int)dp, n) : "";

                // 已经带前缀（从旧解锁器提取的 sfo 会是「奖杯：XXX」）就跳过，
                // 避免再出一包变成「奖杯：奖杯：XXX」
                if (cur.StartsWith(prefix, StringComparison.Ordinal))
                    continue;

                byte[] nb = Encoding.UTF8.GetBytes(prefix + cur + "\0");
                if (nb.Length > max)
                {
                    // 前缀本身放不下就跳过这个字段（STITLE 有时只有几十字节）
                    int pbytes = Encoding.UTF8.GetByteCount(prefix);
                    if (pbytes + 1 > max)
                        continue;

                    StringBuilder sb = new StringBuilder(prefix);
                    int usedBytes = pbytes;
                    foreach (char ch in cur)
                    {
                        int cb = Encoding.UTF8.GetByteCount(ch.ToString());
                        if (usedBytes + cb + 1 > max)
                            break;
                        sb.Append(ch);
                        usedBytes += cb;
                    }
                    nb = Encoding.UTF8.GetBytes(sb.ToString() + "\0");
                }

                Array.Clear(buf, (int)dp, (int)max);
                Buffer.BlockCopy(nb, 0, buf, (int)dp, nb.Length);
                byte[] ul = BitConverter.GetBytes((uint)nb.Length);
                Buffer.BlockCopy(ul, 0, buf, e + 4, 4);
                changed++;
            }
            return changed;
        }

        private static bool IsTitleKey(string key)
        {
            if (key == "TITLE" || key == "STITLE")
                return true;
            if (key == "TITLE_ID")
                return false;
            return key.StartsWith("TITLE_", StringComparison.Ordinal)
                || key.StartsWith("STITLE_", StringComparison.Ordinal);
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
