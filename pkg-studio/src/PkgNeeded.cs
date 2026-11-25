using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
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

        // 零售 PKG key3 的 RSA 私钥，与原版 PS4_Tools.Util.PS4Keys 里的值一致。
        // 这些字节在程序集里是 FieldRVA 的特殊值类型，反射取不到 byte[]，所以直接写死。
        private const string RSA_P = "f967ad9912310c56a22e161c46b34d5b43be42a2f686968042c3c73fc342f58749339f075d6e2c04fde3e1b2ae0a0cf0c7a61ca16350c8099c5124526c5e5ebd1e2706bbbc9e94e135d46db3cb3c68dd68b3fe6ccb8d8220762363b7e96810014edcba275d01c12d805e2baf826bd884b6105286a7898eae9ae289c6f7d587fb";
        private const string RSA_Q = "d7a10f9a8bf2c91195329a8cf0d94047f568a00dbdc1fc432f65f9c3610f257754add758ac8440608d3ff3658975b5c62c511a2f1f22e4431154bec9b4c7b51b050bbc569acd4ad973685e5cfb92b78b0dfff507cab4c89b963c079e3e6b2a11f28ab18ad72e1ba5532406ed50b89067b1e241c69201ee10f061bbfbb27d4a73";
        private const string RSA_MODULUS = "d212fc335f6ddb831609628b0356273782d477853529392d526b8c4c8cfb06c1845be7d4f7bcd24e6245cd2abbd77776453655273fb3f5f98eda4befaa59aeb39bea5498d206326a58312ae0d44f90b50a7decf43a9c52672d99318e0c43e682fe0746e12e50d41f2d2f7ed908ba06b3bf2e203f4e3ffe44ffaa50435791699449158282e40f4c8d9d2cc95b1d64bf888bd4c594e76547841ee57910fb989347b97d8512a640982cf792bc951932ede890560d65c1aa78c62e54fd5f54a1f67ee5e05f61c120b4b9b4330870e4df8956ed012946775f8cb8a9f51e2eb3b9bfe009b78d28d4a6c3b81e1f07ebb4120b95b88530fddc3913d07cdc8fedf9c9a3c1";
        private const string RSA_D = "32d903908fbdb08f572b285e0b8db3ea5cd17ea890888cdd6a80bbb1dfc1f70daa32f0b77ccb88800e8b64b0be4cd60e9b8c1e2a64e1f35cd77601415e935c94fedd4662c31b5ae2a0bc2debc3980aa7b7856970682b644ab31fcc7ddc7c26f477f65cf2ae5a442dd3ab16620419bafb90ffe23050896ecb56b2ebc09116925e308eaec7945dfd35e120f8ad3ebc08bfc036749fd5bb5208fd0666f37ab304f475295de95faa1030b20f5a1ac12ab3fecb21ad80ec8f20091cdbc55894c29cc6ce82653e5790bca98b06b4f072f677df9864f1ecfe372dbcae8c08811fc3c9891ac742824b2edc8e8d73ceb1cc01d90870873c4408ec498f815ae240ff77fc0d";
        private const string RSA_DP = "52cc2da09c9e75e728ee3ddee345d14f941cccc88729453b8d6eab6e2aa7c71543a3048f905febf3384a77fa36b71576b6011a8e258782f155d8c6432ac0e598c932d1946fd901ba0681e06d88f2242a2501645cbff2d999673ef672eee4e2335cf80040e32a9af43d2286443cfb0aa57c3fccf5f116c4ac88b4de6294926a13";
        private const string RSA_DQ = "7c9dad39e0d560149448197f8895d58b80ad858a4b773785d077bbbf89714a72cb726838ec02c67dc6440633511cc0ff958f0d75dc25bb0b7391a96d42d803b768d41e7562a37035797800c8f5ef15b9fc4e475ac870705b5298c0c2584a7096ccb810e12f788b2ba17ff9acdef0bb2be266e3229231215792c4b8f23e762037";
        private const string RSA_INVERSEQ = "459755d422085ef35cb4057afdaa4242ad9a8ca06cbb1d6854546e3e32e3537376f13e01ead3cfebeb233ec0beceec2c895fa8273a4cb7e674bc454c26c825ff34632537e14810c193a6afebbae3a2f13def63d8f4fdd3eee25de933ccadba755c85afcea93dd1a217f3f698b3508e5ef6eb028ea162a7d62cec91ff1540d2e3";

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

        private static byte[] RsaDecryptKey3(byte[] encrypted)
        {
            RSAParameters p = new RSAParameters();
            p.P = HexBytes(RSA_P);
            p.Q = HexBytes(RSA_Q);
            p.Modulus = HexBytes(RSA_MODULUS);
            p.D = HexBytes(RSA_D);
            p.DP = HexBytes(RSA_DP);
            p.DQ = HexBytes(RSA_DQ);
            p.InverseQ = HexBytes(RSA_INVERSEQ);
            p.Exponent = new byte[] { 0x00, 0x01, 0x00, 0x01 };

            using (RSACryptoServiceProvider rsa = new RSACryptoServiceProvider())
            {
                rsa.ImportParameters(p);
                return rsa.Decrypt(encrypted, false);
            }
        }

        private static byte[] HexBytes(string s)
        {
            byte[] b = new byte[s.Length / 2];
            for (int i = 0; i < b.Length; i++)
                b[i] = Convert.ToByte(s.Substring(i * 2, 2), 16);
            return b;
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
