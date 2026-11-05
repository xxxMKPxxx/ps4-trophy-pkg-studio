using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;

namespace PkgStudio
{
    /// <summary>
    /// 直接读远端（FTP 上）的 .pkg —— 只读包头、条目表和需要的几个小文件。
    ///
    /// 依据 PS4 PKG 格式（头为大端）：
    ///   0x000 magic  = 0x7F434E54
    ///   0x010 entry_count（条目数）
    ///   0x018 table_offset（条目表在包内的偏移）
    ///   0x01C ent_data_size（条目表字节数）
    /// 每条条目 0x20 字节：
    ///   0x00 id / 0x04 名字偏移 / 0x08 flags1 / 0x0C flags2 / 0x10 数据偏移 / 0x14 大小
    ///
    /// 特殊文件没有名字，靠 id 认：0x0402 = nptitle.dat，0x0403 = npbind.dat。
    /// param.sfo 和 trophy00.trp 有名字，去 0x0200 号条目（名字表）里按偏移取字符串。
    ///
    /// 全程用 FTP 的 REST 偏移读，只下几十 KB，不下载整个包。
    /// </summary>
    internal static class RemotePkg
    {
        private const uint MAGIC = 0x7F434E54;
        private const int EntrySize = 0x20;

        private const uint ID_NAME_TABLE = 0x0200;

        public const uint IdNptitle = 0x0402;   // nptitle.dat
        public const uint IdNpbind = 0x0403;    // npbind.dat
        private const uint ID_NPTITLE = IdNptitle;
        private const uint ID_NPBIND = IdNpbind;

        internal class Entry
        {
            public uint Id;
            public uint NameOffset;
            public uint Offset;
            public uint Size;
            public uint Flags1;
            public uint Flags2;

            /// <summary>flags1 最高位为 1 表示这个条目的数据是加密的（fpkg 里常见）。</summary>
            public bool Encrypted { get { return (Flags1 & 0x80000000u) != 0; } }
        }

        internal class Index
        {
            public long FileSize;
            public uint EntryCount;
            public uint TableOffset;
            public uint NameTableOffset;
            public uint NameTableSize;
            public List<Entry> Entries = new List<Entry>();
            public byte[] NameTable;
        }

        private static readonly Dictionary<string, Index> Cache =
            new Dictionary<string, Index>(StringComparer.OrdinalIgnoreCase);

        /// <summary>取 param.sfo 的字节。</summary>
        public static byte[] GetParam(string pkgUrl)
        {
            return GetByName(pkgUrl, "param.sfo");
        }

        /// <summary>取 nptitle.dat / npbind.dat（没有名字，按 id 认）。encrypted 说明原始条目是密文。</summary>
        public static byte[] GetSpecial(string pkgUrl, uint id, out bool encrypted)
        {
            encrypted = false;
            try
            {
                Index idx = GetIndex(pkgUrl);
                if (idx == null)
                    return null;

                foreach (Entry e in idx.Entries)
                {
                    if (e.Id != id || e.Size == 0)
                        continue;

                    encrypted = e.Encrypted;
                    byte[] d = ReadRange(pkgUrl, e.Offset, (int)e.Size);
                    Log.Write("远程读 " + IdName(id) + " 成功 (" + e.Size + "B"
                        + (encrypted ? "，加密条目，需要解密" : "") + ")");
                    return d;
                }
                Log.Write("远程包内没有 id=0x" + id.ToString("X4") + " (" + IdName(id) + ")");
                return null;
            }
            catch (Exception ex)
            {
                Log.Write("远程读 " + IdName(id) + " 失败: " + ex.Message);
                return null;
            }
        }

        /// <summary>这个 id 的条目是不是加密的；条目不存在返回 null。</summary>
        public static bool? IsEncrypted(string pkgUrl, uint id)
        {
            try
            {
                Index idx = GetIndex(pkgUrl);
                if (idx == null)
                    return null;

                foreach (Entry e in idx.Entries)
                {
                    if (e.Id == id && e.Size > 0)
                        return e.Encrypted;
                }
                return null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>按文件名取（param.sfo / trophy00.trp 这种有名字的条目）。</summary>
        public static byte[] GetByName(string pkgUrl, string fileName)
        {
            try
            {
                Index idx = GetIndex(pkgUrl);
                if (idx == null || idx.NameTable == null)
                    return null;

                foreach (Entry e in idx.Entries)
                {
                    if (e.Size == 0 || e.Id == ID_NAME_TABLE || e.Id == 0x0001 || e.Id == 0x0100)
                        continue;

                    string name = NameAt(idx, e.NameOffset);
                    if (name == null)
                        continue;

                    if (name.EndsWith("/" + fileName, StringComparison.OrdinalIgnoreCase) ||
                        name.Equals(fileName, StringComparison.OrdinalIgnoreCase))
                    {
                        byte[] d = ReadRange(pkgUrl, e.Offset, (int)e.Size);
                        Log.Write("远程读 " + name + " 成功 (" + e.Size + "B)");
                        return d;
                    }
                }

                Log.Write("远程包里没找到 " + fileName + "（已扫描 " + idx.Entries.Count + " 个条目）");
                return null;
            }
            catch (Exception ex)
            {
                Log.Write("远程读 " + fileName + " 失败: " + ex.Message);
                return null;
            }
        }

        // ---------------- 内部 ----------------

        private static string IdName(uint id)
        {
            if (id == ID_NPTITLE) return "nptitle.dat";
            if (id == ID_NPBIND) return "npbind.dat";
            return "0x" + id.ToString("X4");
        }

        private static Index GetIndex(string pkgUrl)
        {
            lock (Cache)
            {
                Index hit;
                if (Cache.TryGetValue(pkgUrl, out hit))
                    return hit;
            }

            Index idx = BuildIndex(pkgUrl);

            lock (Cache)
            {
                Cache[pkgUrl] = idx;
            }
            return idx;
        }

        private static Index BuildIndex(string pkgUrl)
        {
            long size = RemoteSize(pkgUrl);
            if (size <= 0)
            {
                Log.Write("远程读不到文件大小: " + pkgUrl);
                return null;
            }

            byte[] head = ReadRange(pkgUrl, 0, 0x1000);
            if (head == null || head.Length < 0x40)
            {
                Log.Write("远程读取包头失败: " + pkgUrl);
                return null;
            }

            uint magic = U32(head, 0x000);
            if (magic != MAGIC)
            {
                Log.Write("不是 PS4 pkg（magic=0x" + magic.ToString("X8") + "）: " + pkgUrl);
                return null;
            }

            Index idx = new Index();
            idx.FileSize = size;
            idx.EntryCount = U32(head, 0x010);
            idx.TableOffset = U32(head, 0x018);
            uint entSize = U32(head, 0x01C);

            Log.Write("远程包头: size=" + size + " entries=" + idx.EntryCount
                + " table=" + idx.TableOffset + " entBytes=" + entSize);

            if (idx.EntryCount == 0 || idx.EntryCount > 4096)
            {
                Log.Write("条目数不合理，放弃远程解析");
                return null;
            }

            int need = (int)idx.EntryCount * EntrySize;

            // table_offset 通常是绝对偏移；个别包是相对包尾，两种都试
            byte[] table = ReadRange(pkgUrl, (long)idx.TableOffset, need);
            if (!LooksLikeTable(table))
            {
                long alt = size - idx.TableOffset;
                if (alt >= 0 && alt != idx.TableOffset)
                {
                    Log.Write("按绝对偏移读到的条目表不合理，改按“距包尾”解释: " + alt);
                    byte[] t2 = ReadRange(pkgUrl, alt, need);
                    if (LooksLikeTable(t2))
                    {
                        table = t2;
                        idx.TableOffset = (uint)alt;
                    }
                }
            }

            if (!LooksLikeTable(table))
            {
                Log.Write("条目表解析失败（前 8 字节: " + Hex(table, 8) + "），放弃远程解析");
                return null;
            }

            for (int i = 0; i < idx.EntryCount; i++)
            {
                int o = i * EntrySize;
                Entry e = new Entry();
                e.Id = U32(table, o + 0x00);
                e.NameOffset = U32(table, o + 0x04);
                e.Flags1 = U32(table, o + 0x08);
                e.Flags2 = U32(table, o + 0x0C);
                e.Offset = U32(table, o + 0x10);
                e.Size = U32(table, o + 0x14);

                if (e.Size == 0 || e.Offset == 0 || e.Offset >= size)
                    continue;

                idx.Entries.Add(e);

                if (e.Id == ID_NAME_TABLE)
                {
                    idx.NameTableOffset = e.Offset;
                    idx.NameTableSize = e.Size;
                }
            }

            // 名字表：一般几十 KB，整块读
            if (idx.NameTableSize > 0 && idx.NameTableSize <= 8 * 1024 * 1024)
            {
                idx.NameTable = ReadRange(pkgUrl, idx.NameTableOffset, (int)idx.NameTableSize);
                Log.Write("名字表 " + idx.NameTableSize + "B"
                    + (idx.NameTable == null ? " 读取失败" : " 已读取"));
            }
            else if (idx.NameTableSize > 0)
            {
                Log.Write("名字表太大(" + idx.NameTableSize + "B)，不整块读，只按偏移取");
            }

            return idx;
        }

        private static string NameAt(Index idx, uint off)
        {
            if (idx.NameTable == null)
                return null;
            if (off >= idx.NameTable.Length)
                return null;

            int end = (int)off;
            while (end < idx.NameTable.Length && idx.NameTable[end] != 0)
                end++;

            if (end <= off)
                return null;

            return Encoding.UTF8.GetString(idx.NameTable, (int)off, end - (int)off);
        }

        private static bool LooksLikeTable(byte[] t)
        {
            if (t == null || t.Length < EntrySize)
                return false;

            uint id = U32(t, 0);
            // 条目表里一定会有 0x0001(digests) / 0x0100(metas) / 0x0200(names) 之一
            return id == 0x0001 || id == 0x0100 || id == 0x0200 || id == 0x0400
                || (id >= 0x1000 && id <= 0x1400);
        }

        private static string Hex(byte[] b, int n)
        {
            if (b == null) return "(null)";
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < b.Length && i < n; i++)
                sb.Append(b[i].ToString("X2")).Append(' ');
            return sb.ToString().Trim();
        }

        private static uint U32(byte[] b, int off)
        {
            // PKG 头与条目表都是大端
            return ((uint)b[off] << 24) | ((uint)b[off + 1] << 16)
                 | ((uint)b[off + 2] << 8) | b[off + 3];
        }

        private static long RemoteSize(string url)
        {
            FtpWebRequest req = (FtpWebRequest)WebRequest.Create(url);
            req.Credentials = new NetworkCredential("anonymous", "anonymous@");
            req.Method = WebRequestMethods.Ftp.GetFileSize;
            req.UsePassive = true;
            req.UseBinary = true;
            req.KeepAlive = false;
            req.Timeout = 15000;
            req.ReadWriteTimeout = 15000;

            using (FtpWebResponse resp = (FtpWebResponse)req.GetResponse())
                return resp.ContentLength;
        }

        /// <summary>用 FTP 的 REST 从指定偏移只读 count 字节。</summary>
        private static byte[] ReadRange(string url, long offset, int count)
        {
            if (count <= 0)
                return new byte[0];

            FtpWebRequest req = (FtpWebRequest)WebRequest.Create(url);
            req.Credentials = new NetworkCredential("anonymous", "anonymous@");
            req.Method = WebRequestMethods.Ftp.DownloadFile;
            req.UsePassive = true;
            req.UseBinary = true;
            req.KeepAlive = false;
            req.Timeout = 15000;
            req.ReadWriteTimeout = 15000;
            req.ContentOffset = offset;

            using (FtpWebResponse resp = (FtpWebResponse)req.GetResponse())
            using (Stream s = resp.GetResponseStream())
            {
                byte[] buf = new byte[count];
                int got = 0;

                while (got < count)
                {
                    int n = s.Read(buf, got, count - got);
                    if (n <= 0)
                        break;
                    got += n;
                }

                if (got == count)
                    return buf;

                byte[] part = new byte[got];
                Buffer.BlockCopy(buf, 0, part, 0, got);
                return part;
            }
        }
    }
}
