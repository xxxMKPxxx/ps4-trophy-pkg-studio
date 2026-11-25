using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace PkgStudio
{
    /// <summary>
    /// 「连接解锁」工具：直连 PS4 的 GoldHEN FTP，读取 trophy_local.db，
    /// 给指定奖杯写入解锁状态与自定义时间戳。
    ///
    /// ★ 安全规则（实测验证，见项目日志 2026-11-26）：
    ///   只改 tbl_trophy_flag 的 unlocked / time_unlocked / time_unlocked_uc，
    ///   以及 tbl_trophy_title 的进度统计 5 个字段。
    ///   绝不改 revision / conf_file_revision / title / instsrc_digest 等身份字段——
    ///   改了服务会判定记录与 conf/img 不符，整体重建（第一次实验就是这样失败的）。
    ///   实测：只改上述字段，重启 + 打开奖杯页后均保持，UI 显示自定义时间。
    ///
    /// ★ 写入前必定在主机留 .bak 备份；只处理用户明确选中的游戏/奖杯。
    /// ★ 不改 trophy.img（加密卷），不连 PSN（服务器是最终裁决）。
    /// </summary>
    internal class TrophyEditor : Form
    {
        private const string DB_REMOTE = "/user/home/10000000/trophy/db/trophy_local.db";
        private static readonly string[] UIDS = { "10000000", "10000002", "10000004", "1d8fccf0" };

        private TextBox txtHost;
        private Button btnConnect;
        private ComboBox cmbGame;
        private DataGridView grid;
        private TextBox txtTime;
        private DateTimePicker dtp;
        private Button btnApplyTime;
        private Button btnImport;
        private Button btnWrite;
        private CheckBox chkPlatinum;
        private ToolStripStatusLabel lblStatus;
        private TextBox txtLog;

        private string localDb;                 // 拉下来的 db 本地副本
        private string selectedNp;              // 当前选中的 trophy_title_id
        private bool dirty;                     // 是否有未写回的改动

        private class TrophyRow
        {
            public int TrophyId;
            public string Grade;
            public string Title;
            public bool Unlocked;
            public string Time;       // PS4 格式 "2024-03-15T12:34:56.00Z"
            public string Revision;   // 只读，展示用
        }
        private readonly List<TrophyRow> rows = new List<TrophyRow>();

        public TrophyEditor(string host)
        {
            Text = "连接解锁 · PS4 奖杯时间戳编辑";
            ClientSize = new Size(1060, 720);
            MinimumSize = new Size(880, 560);
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Microsoft YaHei UI", 9f);

            BuildUi();
            if (!string.IsNullOrWhiteSpace(host)) txtHost.Text = host.Trim();
            Log("提示：先点「连接并读取」，选游戏 → 选奖杯 → 填时间 → 写入 PS4");
            Log("安全：只改解锁状态和时间，不动身份字段；写入前自动在主机备份 db");
        }

        private void BuildUi()
        {
            // ---- 顶栏：IP + 连接 ----
            Panel top = new Panel();
            top.Dock = DockStyle.Top;
            top.Height = 52;
            top.BackColor = Color.FromArgb(247, 249, 252);
            top.Padding = new Padding(12, 10, 12, 8);

            Label ipLabel = new Label { Text = "PS4 IP", AutoSize = true, Location = new Point(14, 15) };
            txtHost = new TextBox { Text = "192.168.1.10", Width = 140, Location = new Point(62, 11), BorderStyle = BorderStyle.FixedSingle };
            btnConnect = MakeBtn("连接并读取", false);
            btnConnect.Location = new Point(212, 8);
            btnConnect.Click += OnConnect;

            Button btnRefresh = MakeBtn("重新读取", false);
            btnRefresh.Location = new Point(330, 8);
            btnRefresh.Click += delegate { if (File.Exists(localDb)) LoadGames(); else OnConnect(null, null); };

            top.Controls.Add(ipLabel);
            top.Controls.Add(txtHost);
            top.Controls.Add(btnConnect);
            top.Controls.Add(btnRefresh);

            // ---- 主体：左游戏下拉 + 奖杯表；右时间编辑 ----
            Panel body = new Panel();
            body.Dock = DockStyle.Fill;
            body.Padding = new Padding(12, 8, 12, 8);

            Label gameLbl = new Label { Text = "游戏（奖杯集）", AutoSize = true, Location = new Point(14, 8) };
            cmbGame = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 620, Location = new Point(110, 5) };
            cmbGame.SelectedIndexChanged += OnGameChanged;
            body.Controls.Add(gameLbl);
            body.Controls.Add(cmbGame);

            grid = new DataGridView();
            grid.Location = new Point(14, 40);
            grid.Size = new Size(700, 500);
            grid.AllowUserToAddRows = false;
            grid.AllowUserToDeleteRows = false;
            grid.ReadOnly = true;
            grid.RowHeadersVisible = false;
            grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            grid.MultiSelect = false;
            grid.BackgroundColor = Color.White;
            grid.BorderStyle = BorderStyle.FixedSingle;
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "id", HeaderText = "ID", Width = 44 });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "grade", HeaderText = "等级", Width = 48 });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "title", HeaderText = "奖杯名称", Width = 260 });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "unlocked", HeaderText = "状态", Width = 64 });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "time", HeaderText = "解锁时间 (UTC)", Width = 180 });
            grid.SelectionChanged += OnRowSelected;
            body.Controls.Add(grid);

            // 右侧编辑区
            int rx = 730;
            Label editLbl = new Label { Text = "时间编辑（本机时区，写入自动转 UTC）", AutoSize = true, Location = new Point(rx, 40) };
            body.Controls.Add(editLbl);

            Label dateLbl = new Label { Text = "日期", AutoSize = true, Location = new Point(rx, 74) };
            dtp = new DateTimePicker { Format = DateTimePickerFormat.Short, Location = new Point(rx + 46, 70), Width = 150 };
            body.Controls.Add(dateLbl);
            body.Controls.Add(dtp);

            Label timeLbl = new Label { Text = "时间", AutoSize = true, Location = new Point(rx, 108) };
            txtTime = new TextBox { Text = "12:34:56", Width = 100, Location = new Point(rx + 46, 104), BorderStyle = BorderStyle.FixedSingle };
            body.Controls.Add(timeLbl);
            body.Controls.Add(txtTime);

            Label fmtLbl = new Label { Text = "格式 HH:mm:ss（24小时制）", AutoSize = true, ForeColor = Color.Gray, Location = new Point(rx + 152, 108) };
            body.Controls.Add(fmtLbl);

            btnApplyTime = MakeBtn("应用到选中奖杯", true);
            btnApplyTime.Location = new Point(rx, 146);
            btnApplyTime.Click += OnApplyTime;
            body.Controls.Add(btnApplyTime);

            chkPlatinum = new CheckBox { Text = "白金杯也可解锁（谨慎）", AutoSize = true, Location = new Point(rx, 188), Checked = false };
            body.Controls.Add(chkPlatinum);

            btnImport = MakeBtn("导入时间 CSV…", false);
            btnImport.Location = new Point(rx, 224);
            btnImport.Click += OnImport;
            body.Controls.Add(btnImport);

            Label tips = new Label();
            tips.Text = "CSV 格式：ID,YYYY-MM-DD HH:mm:ss\n例：\n1,2024-03-15 12:34:56\n2,2024-03-16 09:00:00\n\n第一行可作表头（含 id/time 字样自动跳过）。\n时间按本机时区，写入转 UTC。\n白金(grade=1)默认跳过，勾选后可解。";
            tips.Location = new Point(rx, 266);
            tips.Size = new Size(300, 180);
            tips.ForeColor = Color.FromArgb(70, 78, 90);
            body.Controls.Add(tips);

            btnWrite = MakeBtn("写入 PS4（自动备份）", true);
            btnWrite.Location = new Point(rx, 470);
            btnWrite.Size = new Size(300, 42);
            btnWrite.Click += OnWrite;
            body.Controls.Add(btnWrite);

            txtLog = new TextBox();
            txtLog.Multiline = true;
            txtLog.ReadOnly = true;
            txtLog.ScrollBars = ScrollBars.Vertical;
            txtLog.Location = new Point(rx, 526);
            txtLog.Size = new Size(300, 160);
            txtLog.BackColor = Color.FromArgb(250, 250, 250);
            body.Controls.Add(txtLog);

            Controls.Add(body);
            Controls.Add(top);

            StatusStrip status = new StatusStrip();
            lblStatus = new ToolStripStatusLabel("就绪");
            status.Items.Add(lblStatus);
            Controls.Add(status);
        }

        private static Button MakeBtn(string text, bool primary)
        {
            Button b = new Button { Text = text, AutoSize = true, MinimumSize = new Size(0, 30), Cursor = Cursors.Hand, FlatStyle = FlatStyle.Flat };
            b.Padding = new Padding(12, 2, 12, 2);
            b.FlatAppearance.BorderSize = 1;
            if (primary)
            {
                b.BackColor = Color.FromArgb(47, 111, 237);
                b.ForeColor = Color.White;
                b.Font = new Font("Microsoft YaHei UI", 9f, FontStyle.Bold);
                b.FlatAppearance.BorderColor = Color.FromArgb(36, 92, 205);
            }
            else
            {
                b.BackColor = Color.White;
                b.ForeColor = Color.FromArgb(32, 38, 48);
                b.FlatAppearance.BorderColor = Color.FromArgb(196, 202, 212);
            }
            return b;
        }

        private void Log(string msg)
        {
            string line = "[" + DateTime.Now.ToString("HH:mm:ss") + "] " + msg;
            if (!IsHandleCreated) return;
            if (txtLog.InvokeRequired) { txtLog.BeginInvoke(new Action(() => txtLog.AppendText(line + "\r\n"))); return; }
            txtLog.AppendText(line + "\r\n");
        }

        private void Status(string s)
        {
            if (!IsHandleCreated) return;
            if (lblStatus.InvokeRequired) { lblStatus.BeginInvoke(new Action(() => lblStatus.Text = s)); return; }
            lblStatus.Text = s;
        }

        /// <summary>必须在 UI 线程弹窗；后台线程调用时切回 UI 线程。</summary>
        private void ShowError(string msg, string title)
        {
            if (InvokeRequired) { BeginInvoke(new Action(() => MessageBox.Show(this, msg, title, MessageBoxButtons.OK, MessageBoxIcon.Error))); return; }
            MessageBox.Show(this, msg, title, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }

        private void ShowInfo(string msg, string title)
        {
            if (InvokeRequired) { BeginInvoke(new Action(() => MessageBox.Show(this, msg, title, MessageBoxButtons.OK, MessageBoxIcon.Information))); return; }
            MessageBox.Show(this, msg, title, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        // ---------------- FTP ----------------
        private static FtpWebRequest Req(string host, string path, string method)
        {
            FtpWebRequest r = (FtpWebRequest)WebRequest.Create("ftp://" + host + ":2121" + path);
            r.Method = method;
            r.Credentials = new NetworkCredential("anonymous", "anonymous");
            r.UseBinary = true;
            r.KeepAlive = false;
            r.Timeout = 60000;
            r.ReadWriteTimeout = 60000;
            return r;
        }

        private static void FtpDownload(string host, string remote, string local)
        {
            FtpWebRequest r = Req(host, remote, WebRequestMethods.Ftp.DownloadFile);
            using (FtpWebResponse resp = (FtpWebResponse)r.GetResponse())
            using (Stream src = resp.GetResponseStream())
            using (FileStream dst = File.Create(local))
                src.CopyTo(dst);
        }

        private static void FtpUpload(string host, string remote, string local)
        {
            FtpWebRequest r = Req(host, remote, WebRequestMethods.Ftp.UploadFile);
            using (FileStream src = File.OpenRead(local))
            using (Stream dst = r.GetRequestStream())
                src.CopyTo(dst);
            using (FtpWebResponse resp = (FtpWebResponse)r.GetResponse())
                if (resp.StatusCode != FtpStatusCode.CommandOK && resp.StatusCode != FtpStatusCode.ClosingData)
                    throw new Exception("上传返回 " + resp.StatusCode);
        }

        /// <summary>找一个存在 trophy/db 目录的 uid（多数机器是 10000000）。</summary>
        private string FindUid(string host)
        {
            foreach (string uid in UIDS)
            {
                try
                {
                    FtpWebRequest r = Req(host, "/user/home/" + uid + "/trophy/db/trophy_local.db", WebRequestMethods.Ftp.GetFileSize);
                    using (FtpWebResponse resp = (FtpWebResponse)r.GetResponse())
                    {
                        if (resp.ContentLength > 0) return uid;
                    }
                }
                catch { }
            }
            throw new Exception("在 " + UIDS.Length + " 个用户目录下都没找到 trophy_local.db");
        }

        // ---------------- 连接 / 读取 ----------------
        private void OnConnect(object sender, EventArgs e)
        {
            string host = txtHost.Text.Trim();
            if (host.Length == 0) { MessageBox.Show("先填 PS4 的 IP", "连接解锁"); return; }

            btnConnect.Enabled = false;
            Status("连接中…");
            Task.Run(() =>
            {
                try
                {
                    string uid = FindUid(host);
                    Log("找到用户目录: " + uid);

                    string tmp = Path.Combine(Path.GetTempPath(), "pkgstudio", "trophy_edit");
                    Directory.CreateDirectory(tmp);
                    string local = Path.Combine(tmp, "trophy_local.db");
                    FtpDownload(host, "/user/home/" + uid + "/trophy/db/trophy_local.db", local);
                    Log("已下载 trophy_local.db (" + new FileInfo(local).Length + " 字节)");

                    BeginInvoke(new Action(() =>
                    {
                        localDb = local;
                        dirty = false;
                        LoadGames();
                        Status("已连接 " + host + " · " + uid);
                    }));
                }
                catch (Exception ex)
                {
                    Log("连接失败: " + ex.Message);
                    BeginInvoke(new Action(() => { Status("连接失败"); ShowError(ex.Message, "连接失败"); }));
                }
                finally { BeginInvoke(new Action(() => btnConnect.Enabled = true)); }
            });
        }

        private void LoadGames()
        {
            cmbGame.Items.Clear();
            rows.Clear();
            grid.Rows.Clear();
            selectedNp = null;

            using (Sqlite3 db = new Sqlite3(localDb, true))
            {
                var t = db.Query("SELECT trophy_title_id, title, trophy_num, unlocked_trophy_num FROM tbl_trophy_title ORDER BY title COLLATE NOCASE");
                for (int i = 1; i < t.Count; i++)
                {
                    string np = t[i][0];
                    string name = t[i][1];
                    string n = t[i][2], u = t[i][3];
                    cmbGame.Items.Add(new GameEntry(np, name + "  [" + np + "]  " + u + "/" + n));
                }
            }
            Log("读取到 " + cmbGame.Items.Count + " 个奖杯集");
            if (cmbGame.Items.Count > 0) cmbGame.SelectedIndex = 0;
        }

        private class GameEntry
        {
            public readonly string Np;
            public readonly string Label;
            public GameEntry(string np, string label) { Np = np; Label = label; }
            public override string ToString() { return Label; }
        }

        private void OnGameChanged(object sender, EventArgs e)
        {
            if (cmbGame.SelectedItem == null) return;
            selectedNp = ((GameEntry)cmbGame.SelectedItem).Np;
            LoadTrophies();
        }

        private void LoadTrophies()
        {
            grid.Rows.Clear();
            rows.Clear();
            using (Sqlite3 db = new Sqlite3(localDb, true))
            {
                // 用参数化不现实（get_table 只吃整条 SQL），np 来自 db 自身，安全
                var t = db.Query("SELECT trophyid, grade, title, unlocked, time_unlocked, revision " +
                                 "FROM tbl_trophy_flag WHERE trophy_title_id='" + Esc(selectedNp) + "' ORDER BY trophyid");
                for (int i = 1; i < t.Count; i++)
                {
                    var r = new TrophyRow
                    {
                        TrophyId = int.Parse(t[i][0]),
                        Grade = GradeName(t[i][1]),
                        Title = t[i][2],
                        Unlocked = t[i][3] == "1",
                        Time = t[i][4],
                        Revision = t[i][5]
                    };
                    rows.Add(r);
                    grid.Rows.Add(r.TrophyId.ToString(), r.Grade, r.Title,
                        r.Unlocked ? "已解锁" : "未解锁", r.Unlocked ? ToLocal(r.Time) : "");
                }
            }
            Status("已载入 " + rows.Count + " 个奖杯");
        }

        private static string Esc(string s) { return (s ?? "").Replace("'", "''"); }

        private static string GradeName(string g)
        {
            switch (g)
            {
                case "1": return "白金";
                case "2": return "金";
                case "3": return "银";
                case "4": return "铜";
                default: return g;
            }
        }

        /// <summary>PS4 存的 UTC 字符串 → 本机时间显示</summary>
        private static string ToLocal(string utc)
        {
            DateTime d;
            if (DateTime.TryParse(utc, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out d))
                return d.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
            return utc;
        }

        private void OnRowSelected(object sender, EventArgs e)
        {
            if (grid.SelectedRows.Count == 0) return;
            int idx = grid.SelectedRows[0].Index;
            if (idx < 0 || idx >= rows.Count) return;
            TrophyRow r = rows[idx];
            if (r.Unlocked && !string.IsNullOrEmpty(r.Time))
            {
                DateTime d;
                if (DateTime.TryParse(r.Time, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out d))
                {
                    d = d.ToLocalTime();
                    dtp.Value = d.Date;
                    txtTime.Text = d.ToString("HH:mm:ss");
                }
            }
        }

        // ---------------- 时间应用 / 导入 ----------------
        private void OnApplyTime(object sender, EventArgs e)
        {
            if (grid.SelectedRows.Count == 0) { MessageBox.Show("先选中一个奖杯"); return; }
            int idx = grid.SelectedRows[0].Index;
            TrophyRow r = rows[idx];

            DateTime local;
            if (!DateTime.TryParseExact(txtTime.Text.Trim(), "HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out local))
            {
                MessageBox.Show("时间格式应为 HH:mm:ss，例如 12:34:56", "格式错误");
                return;
            }
            DateTime stamp = dtp.Value.Date.Add(local.TimeOfDay);

            if (r.TrophyId == 0 || r.Grade == "白金")
            {
                if (!chkPlatinum.Checked)
                {
                    MessageBox.Show("这是白金杯，默认跳过（其他杯全收集后系统会自动授予）。\n如确实要写，勾选「白金杯也可解锁」。", "白金杯");
                    return;
                }
            }

            r.Unlocked = true;
            r.Time = ToUtc(stamp);
            dirty = true;
            RefreshRow(idx);
            Log("标记解锁: ID=" + r.TrophyId + " 「" + r.Title + "」 → " + r.Time);
        }

        private static string ToUtc(DateTime local)
        {
            DateTime utc = local.ToUniversalTime();
            return utc.ToString("yyyy-MM-dd'T'HH:mm:ss.00'Z'", CultureInfo.InvariantCulture);
        }

        private void RefreshRow(int idx)
        {
            TrophyRow r = rows[idx];
            grid.Rows[idx].Cells[3].Value = r.Unlocked ? "已解锁" : "未解锁";
            grid.Rows[idx].Cells[4].Value = r.Unlocked ? ToLocal(r.Time) : "";
            grid.Rows[idx].DefaultCellStyle.BackColor = r.Unlocked ? Color.FromArgb(235, 250, 235) : Color.White;
        }

        private void OnImport(object sender, EventArgs e)
        {
            using (OpenFileDialog dlg = new OpenFileDialog())
            {
                dlg.Title = "导入时间 CSV";
                dlg.Filter = "CSV 文件|*.csv|文本文件|*.txt|所有文件|*.*";
                if (dlg.ShowDialog(this) != DialogResult.OK) return;

                int applied = 0, skipped = 0;
                var lines = File.ReadAllLines(dlg.FileName, Encoding.UTF8);
                foreach (string raw in lines)
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#") || line.StartsWith("//")) continue;
                    string[] parts = line.Split(',');
                    if (parts.Length < 2) { skipped++; continue; }
                    int id;
                    if (!int.TryParse(parts[0].Trim(), out id)) { skipped++; continue; } // 表头自动跳过
                    DateTime local;
                    if (!DateTime.TryParse(parts[1].Trim(), CultureInfo.InvariantCulture, DateTimeStyles.None, out local)) { skipped++; continue; }

                    TrophyRow r = rows.Find(x => x.TrophyId == id);
                    if (r == null) { skipped++; continue; }
                    if ((r.TrophyId == 0 || r.Grade == "白金") && !chkPlatinum.Checked) { skipped++; continue; }

                    r.Unlocked = true;
                    r.Time = ToUtc(local);
                    applied++;
                    RefreshRow(rows.IndexOf(r));
                }
                dirty = applied > 0;
                Log("导入完成: 应用 " + applied + " 条, 跳过 " + skipped + " 条");
                MessageBox.Show("导入完成：应用 " + applied + " 条，跳过 " + skipped + " 条", "导入 CSV");
            }
        }

        // ---------------- 写入 ----------------
        private void OnWrite(object sender, EventArgs e)
        {
            if (string.IsNullOrEmpty(localDb)) { MessageBox.Show("先连接并读取"); return; }
            if (string.IsNullOrEmpty(selectedNp)) { MessageBox.Show("先选一个游戏"); return; }
            if (!dirty) { MessageBox.Show("没有任何改动，不用写入"); return; }

            int unlockCount = rows.FindAll(r => r.Unlocked).Count;
            if (MessageBox.Show(this,
                "将写入游戏 [" + selectedNp + "]：\n" +
                "  " + rows.Count + " 个奖杯中，" + unlockCount + " 个标记为已解锁（含手动/导入的时间）\n\n" +
                "写入前会自动在 PS4 上备份数据库。\n" +
                "只改解锁状态和时间，不动身份字段。\n\n" +
                "确认写入？", "写入 PS4", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return;

            btnWrite.Enabled = false;
            Status("写入中…");
            string host = txtHost.Text.Trim();
            Task.Run(() =>
            {
                try
                {
                    string uid = FindUid(host);
                    string remote = "/user/home/" + uid + "/trophy/db/trophy_local.db";
                    string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
                    string backup = remote + ".bak-trophyedit-" + stamp;

                    // 1) 从主机再下一份最新的，避免覆盖期间别人的写入
                    string fresh = Path.Combine(Path.GetDirectoryName(localDb), "fresh_" + stamp + ".db");
                    FtpDownload(host, remote, fresh);
                    Log("已下载主机最新 db 用于合并写入");

                    // 2) 主机留备份
                    FtpUpload(host, backup, fresh);
                    Log("主机备份: " + backup);

                    // 3) 在最新副本上只改必要的字段
                    using (Sqlite3 db = new Sqlite3(fresh))
                    {
                        // 确认游戏记录存在（必须是系统注册过的真实记录）
                        string exists = db.Scalar("SELECT COUNT(*) FROM tbl_trophy_title WHERE trophy_title_id='" + Esc(selectedNp) + "'");
                        if (exists != "1") throw new Exception("游戏记录不存在或重复，拒绝写入（请先启动一次游戏让系统注册）");

                        foreach (TrophyRow r in rows)
                        {
                            if (!r.Unlocked) continue;
                            // ★ 只改 unlocked + 两个时间字段；revision 等一概不动
                            db.Exec("UPDATE tbl_trophy_flag SET unlocked=1, " +
                                    "time_unlocked='" + Esc(r.Time) + "', " +
                                    "time_unlocked_uc='" + Esc(UcTime(r.Time)) + "' " +
                                    "WHERE trophy_title_id='" + Esc(selectedNp) + "' AND trophyid=" + r.TrophyId);
                        }

                        // title 统计字段：按 grade 重算
                        int unlocked = rows.FindAll(r => r.Unlocked).Count;
                        int plat = rows.FindAll(r => r.Unlocked && r.Grade == "白金").Count;
                        int gold = rows.FindAll(r => r.Unlocked && r.Grade == "金").Count;
                        int silver = rows.FindAll(r => r.Unlocked && r.Grade == "银").Count;
                        int bronze = rows.FindAll(r => r.Unlocked && r.Grade == "铜").Count;
                        // progress 用整数百分比
                        int total = rows.Count;
                        int progress = total > 0 ? (int)Math.Round(unlocked * 100.0 / total) : 0;
                        db.Exec("UPDATE tbl_trophy_title SET progress=" + progress +
                                ", unlocked_trophy_num=" + unlocked +
                                ", unlocked_platinum_num=" + plat +
                                ", unlocked_gold_num=" + gold +
                                ", unlocked_silver_num=" + silver +
                                ", unlocked_bronze_num=" + bronze +
                                ", sync_req_num=" + unlocked +
                                ", sync_pending_num=" + unlocked +
                                " WHERE trophy_title_id='" + Esc(selectedNp) + "'");
                        Log("统计: " + unlocked + "/" + total + " = " + progress + "%");
                    }

                    // 4) 校验完整性后上传
                    using (Sqlite3 db = new Sqlite3(fresh, true))
                    {
                        string ic = db.Scalar("PRAGMA integrity_check");
                        if (ic != "ok") throw new Exception("写入后完整性校验失败: " + ic);
                    }
                    FtpUpload(host, remote, fresh);
                    Log("✅ 写入完成。请重启 PS4 或打开奖杯页验证。");
                    BeginInvoke(new Action(() =>
                    {
                        dirty = false;
                        Status("写入完成");
                        ShowInfo("写入完成！\n\n请重启 PS4 或打开奖杯页查看。\n如想撤销：主机上有备份 " + backup, "完成");
                    }));
                }
                catch (Exception ex)
                {
                    Log("写入失败: " + ex.Message);
                    BeginInvoke(new Action(() => { Status("写入失败"); ShowError(ex.Message, "写入失败"); }));
                }
                finally { BeginInvoke(new Action(() => btnWrite.Enabled = true)); }
            });
        }

        /// <summary>time_unlocked_uc：比主时间早约 0.84 秒（PS4 原始高精度字段），不影响显示。</summary>
        private static string UcTime(string utc)
        {
            DateTime d;
            if (DateTime.TryParse(utc, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out d))
                return d.AddMilliseconds(-840).ToString("yyyy-MM-dd'T'HH:mm:ss.ff'Z'", CultureInfo.InvariantCulture);
            return utc;
        }
    }
}
