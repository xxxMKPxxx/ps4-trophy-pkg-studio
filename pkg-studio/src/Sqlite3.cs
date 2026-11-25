using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.IO;

namespace PkgStudio
{
    /// <summary>
    /// SQLite 读写封装——基于 System.Data.SQLite（x86 mixed-mode，随 PS4_Tools 产物一起分发）。
    ///
    /// 为什么不再用 P/Invoke 直接调 sqlite3.dll：
    ///   PkgStudio 依赖 PS4_Tools.dll 等 x86 程序集，进程固定 32 位；
    ///   原生 sqlite3.dll 的位数/运行库依赖极易踩坑（0x8007000B 格式错误闪退），
    ///   而这份 System.Data.SQLite.dll 是上游已在用的成熟库，位数匹配、无需 NuGet。
    ///
    /// PS4 的 trophy_local.db 是标准 SQLite 3，里面是 UTF-8 繁体奖杯名，
    /// System.Data.SQLite 默认按 UTF-8 处理，无需额外 marshaling。
    /// </summary>
    internal sealed class Sqlite3 : IDisposable
    {
        private readonly SQLiteConnection conn;

        public Sqlite3(string path, bool readOnly = false)
        {
            if (!File.Exists(path) && readOnly)
                throw new Exception("数据库文件不存在: " + path);

            // 只读场景用 Read Only 模式，避免误写；不指定 Pooling 防止文件被独占
            string cs = "Data Source=" + path + ";Version=3;";
            if (readOnly) cs += "Read Only=True;";
            conn = new SQLiteConnection(cs);
            conn.Open();
        }

        /// <summary>执行 INSERT/UPDATE/DELETE。</summary>
        public void Exec(string sql)
        {
            using (SQLiteCommand cmd = (SQLiteCommand)new SQLiteCommand("", conn))
            {
                cmd.CommandText = sql;
                cmd.ExecuteNonQuery();
            }
        }

        /// <summary>
        /// 读 SELECT，返回行列表；第一行是列名（调用方按 i=1 开始遍历数据行）。
        /// </summary>
        public List<string[]> Query(string sql)
        {
            var rows = new List<string[]>();
            using (SQLiteCommand cmd = (SQLiteCommand)new SQLiteCommand("", conn))
            {
                cmd.CommandText = sql;
                using (SQLiteDataReader rd = cmd.ExecuteReader())
                {
                    // 第一行：列名
                    var header = new string[rd.FieldCount];
                    for (int c = 0; c < rd.FieldCount; c++) header[c] = rd.GetName(c);
                    rows.Add(header);

                    while (rd.Read())
                    {
                        var row = new string[rd.FieldCount];
                        for (int c = 0; c < rd.FieldCount; c++)
                            row[c] = rd.IsDBNull(c) ? "" : rd.GetValue(c).ToString();
                        rows.Add(row);
                    }
                }
            }
            return rows;
        }

        /// <summary>取单值（第一行第一列），没有结果返回 null。</summary>
        public string Scalar(string sql)
        {
            using (SQLiteCommand cmd = (SQLiteCommand)new SQLiteCommand("", conn))
            {
                cmd.CommandText = sql;
                object v = cmd.ExecuteScalar();
                return v == null || v == DBNull.Value ? null : v.ToString();
            }
        }

        public void Dispose()
        {
            if (conn != null)
            {
                conn.Close();
                conn.Dispose();
            }
        }
    }
}
