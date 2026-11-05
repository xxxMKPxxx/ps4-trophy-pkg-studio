// SfoUtil.cs — 按 UTF-8 正确读写 PS4 param.sfo
//
// 为什么需要它：PS4 的 param.sfo 里字符串一律是 UTF-8，但 PS4_Tools 的
// PARAM_SFO.SaveSFO() 用的是系统 ANSI 代码页（中文 Windows 上是 GBK）。
// 于是 "Unlocker for 龍が如く" 这种标题写回去就变成乱码，XMB 和 pkg 文件名一起坏。
//
// 这里直接按 SFO 二进制结构改字段，不碰其他字节，并且写完回读校验。
//
// SFO 结构：
//   0x00 magic       u32  0x46535000 ("\0PSF")
//   0x04 version     u32
//   0x08 key_start   u32  键表绝对偏移
//   0x0C data_start  u32  数据表绝对偏移
//   0x10 count       u32  条目数
//   0x14 起，每条 16 字节：
//     key_offset u16（相对 key_start）
//     fmt        u16（字符串为 0x0404）
//     len        u32（值的实际长度，字符串含结尾 NUL）
//     max_len    u32（分配长度）
//     data_offset u32（相对 data_start）
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace TrophyUnlocker
{
    public static class SfoUtil
    {
        private const uint PSF_MAGIC = 0x46535000;

        // 排查开关（exe 同目录 unlocker.ini）：
        //   sfofix=1   才重写 param.sfo 的 TITLE（默认关：实测重写会让 PS4 拒装）
        //   cidfix=1   允许自动重建 CONTENT_ID（默认关，只记录不改）
        // 默认两个都关 = 与 1188bf4（验证可安装的那版）行为一致。
        private static readonly bool Disabled = (ReadIni("sfofix") != "1");

        /// <summary>是否启用 param.sfo 重写。默认 false。</summary>
        public static bool Enabled
        {
            get { return !Disabled; }
        }

        /// <summary>是否允许自动改写 CONTENT_ID。默认 false，只记录。</summary>
        public static bool CidFixEnabled
        {
            get { return ReadIni("cidfix") == "1"; }
        }

        private static string ReadIni(string key)
        {
            try
            {
                string p = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "unlocker.ini");
                if (!File.Exists(p))
                    return null;

                foreach (string line in File.ReadAllLines(p))
                {
                    string s = line.Trim();
                    if (s.Length == 0 || s.StartsWith("#"))
                        continue;
                    int eq = s.IndexOf('=');
                    if (eq <= 0)
                        continue;
                    if (s.Substring(0, eq).Trim().ToLowerInvariant() != key)
                        continue;
                    return s.Substring(eq + 1).Trim();
                }
            }
            catch
            {
            }
            return null;
        }

        private sealed class Entry
        {
            public string Key;
            public ushort Fmt;
            public byte[] Data;
        }

        private static List<Entry> Parse(byte[] buf)
        {
            if (buf == null || buf.Length < 20)
                throw new InvalidDataException("SFO 太小");

            if (BitConverter.ToUInt32(buf, 0) != PSF_MAGIC)
                throw new InvalidDataException("不是 SFO 文件");

            uint keyStart  = BitConverter.ToUInt32(buf, 8);
            uint dataStart = BitConverter.ToUInt32(buf, 12);
            uint count     = BitConverter.ToUInt32(buf, 16);

            if (count > 512)
                throw new InvalidDataException("条目数异常: " + count);
            if (keyStart < 20 || keyStart >= buf.Length)
                throw new InvalidDataException("key 表偏移异常");
            if (dataStart > buf.Length)
                throw new InvalidDataException("data 表偏移异常");

            var list = new List<Entry>((int)count);
            for (int i = 0; i < count; i++)
            {
                int e = 20 + i * 16;
                if (e + 16 > buf.Length)
                    throw new InvalidDataException("条目越界");

                ushort keyOff  = BitConverter.ToUInt16(buf, e);
                ushort fmt     = BitConverter.ToUInt16(buf, e + 2);
                uint   len     = BitConverter.ToUInt32(buf, e + 4);
                uint   dataOff = BitConverter.ToUInt32(buf, e + 12);

                long kp = (long)keyStart + keyOff;
                if (kp >= buf.Length)
                    throw new InvalidDataException("key 越界");

                int keyEnd = (int)kp;
                while (keyEnd < buf.Length && buf[keyEnd] != 0)
                    keyEnd++;

                long dp = (long)dataStart + dataOff;
                if (dp + len > buf.Length)
                    throw new InvalidDataException("值越界: " + len);

                Entry ne = new Entry();
                ne.Key = Encoding.ASCII.GetString(buf, (int)kp, keyEnd - (int)kp);
                ne.Fmt = fmt;
                ne.Data = new byte[len];
                Array.Copy(buf, (int)dp, ne.Data, 0, (int)len);
                list.Add(ne);
            }
            return list;
        }

        private static string Decode(byte[] data)
        {
            int n = Array.IndexOf(data, (byte)0);
            if (n < 0) n = data.Length;
            return Encoding.UTF8.GetString(data, 0, n);
        }

        /// <summary>读取字符串字段；找不到返回 null</summary>
        public static string GetString(byte[] sfo, string key)
        {
            foreach (Entry en in Parse(sfo))
            {
                if (en.Key == key)
                    return Decode(en.Data);
            }
            return null;
        }

        /// <summary>按 UTF-8 写入字符串字段；失败时保持原文件不变</summary>
        public static void SetString(string path, string key, string value)
        {
            if (Disabled)
                return;

            byte[] original = File.ReadAllBytes(path);
            byte[] output = SetStringInBytes(original, key, value);

            // 回读校验：结构不对就还原，绝不把坏 SFO 交给打包工具
            try
            {
                string back = GetString(output, key);
                if (back != value)
                    throw new InvalidDataException("回读不一致");
            }
            catch
            {
                File.WriteAllBytes(path, original);
                throw;
            }

            File.WriteAllBytes(path, output);
        }

        private static byte[] SetStringInBytes(byte[] buf, string key, string value)
        {
            List<Entry> list = Parse(buf);
            int idx = list.FindIndex(x => x.Key == key);
            if (idx < 0)
                throw new InvalidDataException("找不到字段: " + key);

            // 字符串值必须带结尾 NUL
            list[idx].Data = Encoding.UTF8.GetBytes(value + "\0");

            // 键表和条目顺序都没动，所以 key 偏移可以原样重建
            var keys = new MemoryStream();
            var keyOffsets = new List<ushort>(list.Count);
            foreach (Entry en in list)
            {
                keyOffsets.Add((ushort)keys.Length);
                byte[] kb = Encoding.ASCII.GetBytes(en.Key);
                keys.Write(kb, 0, kb.Length);
                keys.WriteByte(0);
            }
            while (keys.Length % 4 != 0)
                keys.WriteByte(0);

            var data = new MemoryStream();
            var dataOffsets = new List<uint>(list.Count);
            foreach (Entry en in list)
            {
                dataOffsets.Add((uint)data.Length);
                data.Write(en.Data, 0, en.Data.Length);
                while (data.Length % 4 != 0)
                    data.WriteByte(0);
            }

            uint keyStart  = (uint)(20 + list.Count * 16);
            uint dataStart = keyStart + (uint)keys.Length;

            var outp = new MemoryStream();
            var bw = new BinaryWriter(outp);
            bw.Write(PSF_MAGIC);
            bw.Write(BitConverter.ToUInt32(buf, 4));   // version 原样保留
            bw.Write(keyStart);
            bw.Write(dataStart);
            bw.Write((uint)list.Count);

            for (int i = 0; i < list.Count; i++)
            {
                bw.Write(keyOffsets[i]);
                bw.Write(list[i].Fmt);
                bw.Write((uint)list[i].Data.Length);
                bw.Write((uint)list[i].Data.Length);
                bw.Write(dataOffsets[i]);
            }
            bw.Write(keys.ToArray());
            bw.Write(data.ToArray());
            bw.Flush();
            return outp.ToArray();
        }
    }
}
