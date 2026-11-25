using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace PkgStudio
{
    /// <summary>
    /// 按需读取 PKG：只解析头部和条目表，然后只读 param.sfo / nptitle / npbind /
    /// icon0 / trophyNN 这几个小条目，不把几十 GB 的游戏本体分配进内存。
    ///
    /// 上游 Read_PKG 会对每个条目 new byte[size]，大包里的巨型条目会 OOM，
    /// 靠后的条目还会受 32 位 offset 影响读不到。这里保留同样的解密算法
    /// （key3: RSA 解 key seed + AES-CBC），但只在目标条目上做。
    /// </summary>
    internal static class PkgNeeded
    {
        private const uint ID_ENTRY_KEYS = 0x0010;
        private const uint ID_NPTITLE = 0x0402;
        private const uint ID_NPBIND = 0x0403;
        private const uint ID_PARAM_SFO = 0x1000;
        private const uint ID_ICON0 = 0x1200;
        private const uint ID_TROPHY00 = 0x1400;
        private const uint ID_TROPHY99 = 0x1400 + 99;

        private const int MAX_TARGET_SIZE = 64 * 1024 * 1024;
        private const int ENTRY_SIZE = 32;

        private sealed class Entry
        {
            public uint Id;
            public uint Flags1;
            public uint Flags2;
            public uint Offset;
            public uint Size;
            public byte[] Raw;          // 32 字节原始条目，解密时参与 SHA256
            public bool Encrypted { get { return (Flags1 & 0x80000000u) != 0; } }
            public int KeyIndex { get { return (int)((Flags2 & 0xF000u) >> 12); } }
        }

        /// <summary>成功读到 param.sfo 返回 true；失败/不支持时返回 false 走原版 Read_PKG。</summary>
        public static bool Fill(GameItem it)
        {
            Stopwatch watch = Stopwatch.StartNew();
            try
            {
                using (FileStream fs = new FileStream(it.Origin, FileMode.Open, FileAccess.Read,
                           FileShare.ReadWrite, 1, FileOptions.RandomAccess))
                {
                    byte[] head = ReadAt(fs, 0, 0x40);
                    if (head == null || head.Length < 0x40
                        || head[0] != 0x7F || head[1] != 0x43 || head[2] != 0x4E || head[3] != 0x54)
                        throw new Exception("不是 PS4 PKG（magic 不对）");

                    uint count = BE32(head, 0x10);
                    uint table = BE32(head, 0x18);
                    if (count == 0 || count > 8192)
                        throw new Exception("条目数异常: " + count);
                    if ((long)table + (long)count * ENTRY_SIZE > fs.Length)
                        throw new Exception("条目表超出文件范围");

                    byte[] tableBytes = ReadAt(fs, table, checked((int)(count * ENTRY_SIZE)));
                    List<Entry> targets = new List<Entry>();
                    Entry keysEntry = null;

                    for (int i = 0; i < count; i++)
                    {
                        byte[] raw = new byte[ENTRY_SIZE];
                        Buffer.BlockCopy(tableBytes, i * ENTRY_SIZE, raw, 0, ENTRY_SIZE);
                        Entry e = new Entry
                        {
                            Id = BE32(raw, 0),
                            Flags1 = BE32(raw, 8),
                            Flags2 = BE32(raw, 12),
                            Offset = BE32(raw, 16),
                            Size = BE32(raw, 20),
                            Raw = raw
                        };

                        if (e.Id == ID_ENTRY_KEYS) keysEntry = e;
                        if (e.Id == ID_PARAM_SFO || e.Id == ID_NPTITLE || e.Id == ID_NPBIND
                            || e.Id == ID_ICON0 || (e.Id >= ID_TROPHY00 && e.Id <= ID_TROPHY99))
                            targets.Add(e);
                    }

                    Log.Write("按需读取: " + (fs.Length / 1024 / 1024) + " MB / " + count + " 条目 / 命中 "
                        + targets.Count + " 个目标条目");

                    // 只要有一个加密目标条目，就需要 ENTRY_KEYS 和 0x2400 的解密数据
                    bool needCrypto = false;
                    foreach (Entry e in targets)
                        if (e.Encrypted) needCrypto = true;

                    byte[] passcodeData = null;
                    byte[] keySeed3 = null;
                    if (needCrypto)
                    {
                        passcodeData = PS4_Tools.Util.PS4PkgUtil.Decrypt(ReadAt(fs, 0x2400, 256));
                        if (keysEntry == null || keysEntry.Size < 2048)
                            throw new Exception("加密包缺少 ENTRY_KEYS，无法按需解密");
                        byte[] keysBlob = ReadAt(fs, keysEntry.Offset, 2048);
                        // seedDigest(32) + 7*digest(32) + 7*key(256)，取 key[3]
                        byte[] encKey3 = new byte[256];
                        Buffer.BlockCopy(keysBlob, 32 + 7 * 32 + 3 * 256, encKey3, 0, 256);
                        keySeed3 = RsaDecryptKey3(encKey3);
                    }

                    List<KeyValuePair<int, byte[]>> trophies = new List<KeyValuePair<int, byte[]>>();
                    foreach (Entry e in targets)
                    {
                        if (e.Size == 0)
                            continue;                    // 0KB = 真的一条空数据，不等于“没读到”
                        if (e.Size > MAX_TARGET_SIZE)
                            throw new Exception("目标条目异常巨大: 0x" + e.Id.ToString("X4") + " / " + e.Size + "B");

                        long off = e.Offset;             // 上游用 uint，大包靠后条目会错；目标条目都在前部
                        if (off + e.Size > fs.Length)
                            throw new Exception("目标条目超出文件范围: 0x" + e.Id.ToString("X4"));

                        byte[] data = ReadAt(fs, off, (int)e.Size);
                        if (e.Encrypted)
                        {
                            if (e.KeyIndex != 3)
                                throw new Exception("暂不支持按需解密 key_index=" + e.KeyIndex);
                            data = DecryptKey3(e, data, passcodeData, keySeed3);
                        }

                        switch (e.Id)
                        {
                            case ID_PARAM_SFO: it.Param = data; break;
                            case ID_NPTITLE: it.Nptitle = data; break;
                            case ID_NPBIND: it.Npbind = data; break;
                            case ID_ICON0: it.Icon0 = data; break;
                            default:
                                trophies.Add(new KeyValuePair<int, byte[]>((int)(e.Id - ID_TROPHY00), data));
                                break;
                        }
                    }

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

                    if (it.Param != null)
                    {
                        it.Title = Sfo.Get(it.Param, "TITLE");
                        it.TitleId = Sfo.Get(it.Param, "TITLE_ID");
                        it.ContentId = Sfo.Get(it.Param, "CONTENT_ID");
                        it.Category = Sfo.Get(it.Param, "CATEGORY");
                        it.AppVer = Sfo.Get(it.Param, "APP_VER");
                        it.Kind = GameItem.KindOf(it.Category);
                    }

                    Log.Write("按需读取完成(" + watch.ElapsedMilliseconds + " ms): " + it.Title + " / " + it.TitleId
                        + " / param=" + Size(it.Param)
                        + " nptitle=" + Size(it.Nptitle)
                        + " npbind=" + Size(it.Npbind)
                        + " trophy=" + Size(it.Trophy)
                        + " (共 " + (it.Trophies == null ? 0 : it.Trophies.Count) + " 个 TRP)");

                    return it.Param != null;
                }
            }
            catch (Exception ex)
            {
                Log.Write("按需读取失败，退回原版 Read_PKG: " + ex.Message);
                return false;
            }
        }

        private static byte[] DecryptKey3(Entry e, byte[] data, byte[] passcodeData, byte[] keySeed)
        {
            // 上游：对齐到 16 字节，entry 原始头(32B) + passcode 前 32B 做 SHA256
            int aligned = (data.Length + 15) & ~15;
            byte[] buf = new byte[aligned];
            Buffer.BlockCopy(data, 0, buf, 0, data.Length);

            byte[] hashInput = new byte[64];
            Buffer.BlockCopy(e.Raw, 0, hashInput, 0, 32);
            Buffer.BlockCopy(passcodeData, 0, hashInput, 32, 32);
            byte[] ivKey = PS4_Tools.LibOrbis.Util.Crypto.Sha256(hashInput);

            byte[] iv = new byte[16];
            byte[] key = new byte[16];
            Buffer.BlockCopy(ivKey, 0, iv, 0, 16);
            Buffer.BlockCopy(ivKey, 16, key, 0, 16);

            byte[] plain = new byte[aligned];
            PS4_Tools.LibOrbis.Util.Crypto.AesCbcCfb128Decrypt(plain, buf, aligned, key, iv);

            // 只保留真实长度，去掉 16 字节对齐填充
            byte[] result = new byte[data.Length];
            Buffer.BlockCopy(plain, 0, result, 0, data.Length);
            return result;
        }

        /// <summary>从 PS4_Tools.dll 里取零售 RSA 私钥（类型是 internal，只能反射）。</summary>
        private static byte[] RsaDecryptKey3(byte[] encrypted)
        {
            Assembly asm = typeof(PS4_Tools.PKG.SceneRelated.Unprotected_PKG).Assembly;
            Type t = asm.GetType("PS4_Tools.Util.PS4Keys+ShellCore_Keys+Retail+RSA_PKG_Meta", false);
            if (t == null) throw new Exception("找不到 RSA_PKG_Meta");

            RSAParameters p = new RSAParameters();
            p.P = KeyBytes(t, "C1D52ECFD111255EE610C500024CFEA769DDD62E");
            p.Q = KeyBytes(t, "E89E82AE8092066A5868DFA1219481CBB7897C0B");
            p.Modulus = KeyBytes(t, "9519D939497C79C3633E03861BE500D3570781C5");
            p.D = KeyBytes(t, "5402B87C40B40C0528A1836FC77B1F0B8E85DE80");
            p.DP = KeyBytes(t, "AC6F6DFDBC7A74B9F955875CF9B2783F7BE3E475");
            p.DQ = KeyBytes(t, "EA1E8BD1E1381DB9E0B4481E26D6CBCB7F8BA611");
            p.InverseQ = KeyBytes(t, "1826B7521D2CCA38E28B9E70FB04D6B656F53015");
            p.Exponent = new byte[] { 0x00, 0x01, 0x00, 0x01 };

            using (RSACryptoServiceProvider rsa = new RSACryptoServiceProvider())
            {
                rsa.ImportParameters(p);
                return rsa.Decrypt(encrypted, false);
            }
        }

        private static byte[] KeyBytes(Type t, string name)
        {
            FieldInfo f = t.GetField(name, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (f == null) throw new Exception("缺少 RSA 参数 " + name);
            object v = f.GetValue(null);
            if (v is byte[]) return (byte[])v;
            string s = v as string;
            if (s != null)
            {
                byte[] b = new byte[s.Length / 2];
                for (int i = 0; i < b.Length; i++)
                    b[i] = Convert.ToByte(s.Substring(i * 2, 2), 16);
                return b;
            }
            throw new Exception("RSA 参数类型不认识: " + name);
        }

        private static byte[] ReadAt(FileStream fs, long offset, int count)
        {
            if (count <= 0) return new byte[0];
            byte[] buf = new byte[count];
            fs.Position = offset;
            int got = 0;
            while (got < count)
            {
                int n = fs.Read(buf, got, count - got);
                if (n <= 0) break;
                got += n;
            }
            if (got != count)
                throw new Exception("读取不完整 @0x" + offset.ToString("X") + " 需要 " + count + " 实际 " + got);
            return buf;
        }

        private static uint BE32(byte[] b, int o)
        {
            return ((uint)b[o] << 24) | ((uint)b[o + 1] << 16) | ((uint)b[o + 2] << 8) | b[o + 3];
        }

        private static string Size(byte[] b)
        {
            return b == null ? "无" : (b.Length / 1024) + "KB";
        }
    }
}
