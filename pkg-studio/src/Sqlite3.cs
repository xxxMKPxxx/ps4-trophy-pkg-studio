using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace PkgStudio
{
    /// <summary>
    /// 极简 SQLite 读写封装：直接 P/Invoke 同目录的 sqlite3.dll，
    /// 不引入 NuGet 依赖（CI 只装 MSBuild，PackageReference 还要额外 restore）。
    /// 只需要 5 个 API：open / get_table（读 SELECT）/ exec（写 UPDATE/DDL）/ free_table / close。
    ///
    /// PS4 的 trophy_local.db 是标准 SQLite 3，里面全是 UTF-8 文本（繁体奖杯名），
    /// 所以所有字符串都按 UTF-8 编解码，不能用默认的 ANSI marshaling。
    /// </summary>
    internal sealed class Sqlite3 : IDisposable
    {
        private const string Dll = "sqlite3.dll";
        private IntPtr db = IntPtr.Zero;

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_open_v2(byte[] filename, out IntPtr ppDb, int flags, byte[] zvfs);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_close(IntPtr pDb);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_exec(IntPtr pDb, byte[] sql, IntPtr callback, IntPtr arg, out IntPtr errmsg);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        private static extern void sqlite3_free(IntPtr p);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_get_table(IntPtr pDb, byte[] sql, out IntPtr result,
            out int nrow, out int ncol, out IntPtr errmsg);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        private static extern void sqlite3_free_table(IntPtr result);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr sqlite3_errmsg(IntPtr pDb);

        private const int SQLITE_OK = 0;
        private const int SQLITE_OPEN_READWRITE = 0x00000002;
        private const int SQLITE_OPEN_CREATE = 0x00000004;
        private const int SQLITE_OPEN_URI = 0x00000040;

        public Sqlite3(string path, bool readOnly = false)
        {
            int flags = readOnly ? SQLITE_OPEN_READWRITE : (SQLITE_OPEN_READWRITE | SQLITE_OPEN_CREATE);
            // 用 URI 打开的好处：路径含空格/中文也不会被当参数分隔符
            string uri = "file:" + Uri.EscapeUriString(path.Replace('\\', '/'));
            int rc = sqlite3_open_v2(Utf8(uri), out db, flags | SQLITE_OPEN_URI, null);
            if (rc != SQLITE_OK || db == IntPtr.Zero)
                throw new Exception("打开数据库失败: " + LastError());
        }

        private static byte[] Utf8(string s)
        {
            // sqlite3_* 要的是 NUL 结尾的 UTF-8
            byte[] raw = Encoding.UTF8.GetBytes(s ?? "");
            byte[] buf = new byte[raw.Length + 1];
            Array.Copy(raw, buf, raw.Length);
            return buf;
        }

        private static string PtrToUtf8(IntPtr p)
        {
            if (p == IntPtr.Zero) return "";
            int len = 0;
            while (Marshal.ReadByte(p, len) != 0) len++;
            if (len == 0) return "";
            byte[] buf = new byte[len];
            Marshal.Copy(p, buf, 0, len);
            return Encoding.UTF8.GetString(buf);
        }

        private string LastError()
        {
            if (db == IntPtr.Zero) return "数据库未打开";
            return PtrToUtf8(sqlite3_errmsg(db));
        }

        /// <summary>执行 INSERT/UPDATE/DELETE，返回受影响行数无法直接拿到，用 changes() 查。</summary>
        public void Exec(string sql)
        {
            IntPtr err = IntPtr.Zero;
            int rc = sqlite3_exec(db, Utf8(sql), IntPtr.Zero, IntPtr.Zero, out err);
            if (rc != SQLITE_OK)
            {
                string msg = err != IntPtr.Zero ? PtrToUtf8(err) : LastError();
                if (err != IntPtr.Zero) sqlite3_free(err);
                throw new Exception("SQL 执行失败: " + msg + "\n" + Trim(sql));
            }
        }

        private static string Trim(string s)
        {
            return s.Length > 300 ? s.Substring(0, 300) + "…" : s;
        }

        /// <summary>
        /// 读 SELECT，返回行列表；第一行是列名。
        /// 调用方按列名索引取单元格，避免依赖列顺序。
        /// </summary>
        public List<string[]> Query(string sql)
        {
            IntPtr table = IntPtr.Zero;
            int nrow = 0, ncol = 0;
            IntPtr err = IntPtr.Zero;
            int rc = sqlite3_get_table(db, Utf8(sql), out table, out nrow, out ncol, out err);
            if (rc != SQLITE_OK)
            {
                string msg = err != IntPtr.Zero ? PtrToUtf8(err) : LastError();
                if (err != IntPtr.Zero) sqlite3_free(err);
                if (table != IntPtr.Zero) sqlite3_free_table(table);
                throw new Exception("SQL 查询失败: " + msg + "\n" + Trim(sql));
            }

            var rows = new List<string[]>();
            try
            {
                // 表内存布局：string*[0..ncol-1] 是列名，之后是 nrow*ncol 个数据单元格
                for (int r = -1; r < nrow; r++)
                {
                    var row = new string[ncol];
                    for (int c = 0; c < ncol; c++)
                    {
                        int idx = (r + 1) * ncol + c;
                        row[c] = PtrToUtf8(Marshal.ReadIntPtr(table, idx * IntPtr.Size));
                    }
                    rows.Add(row);
                }
            }
            finally
            {
                sqlite3_free_table(table);
            }
            return rows;
        }

        /// <summary>取单值（第一行第一列），没有结果返回 null。</summary>
        public string Scalar(string sql)
        {
            var rows = Query(sql);
            if (rows.Count < 2 || rows[1].Length == 0) return null;
            return rows[1][0];
        }

        public void Dispose()
        {
            if (db != IntPtr.Zero)
            {
                sqlite3_close(db);
                db = IntPtr.Zero;
            }
        }
    }
}
