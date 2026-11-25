using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace PkgStudio
{
    internal class MainForm : Form
    {
        private DataGridView grid;
        private ListView outputs;
        private TextBox txtHost;
        private TextBox txtOut;
        private ToolStripStatusLabel lblStatus;
        private Button btnBuild;
        private readonly ToolTip tips = new ToolTip();

        private readonly List<GameItem> items = new List<GameItem>();
        private readonly string workRoot;

        public MainForm()
        {
            workRoot = Path.Combine(Path.GetTempPath(), "pkgstudio");
            Directory.CreateDirectory(workRoot);

            Text = "PS4 Trophy PKG Studio";
            ClientSize = new Size(1180, 700);
            MinimumSize = new Size(900, 520);
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Microsoft YaHei UI", 9f);

            BuildUi();

            // 默认输出目录（exe 同目录的 PKG）启动时就建好，第一次打包不用手建
            try { Directory.CreateDirectory(ResolveOutDir(txtOut.Text)); }
            catch (Exception ex) { Log.Write("创建默认输出目录失败: " + ex.Message); }

            // 导入缓存目录只留最近两周，避免越滚越大
            Task.Run(new Action(CleanupCache));
        }

        private void CleanupCache()
        {
            try
            {
                string root = Path.Combine(workRoot, "pkgcache");
                if (!Directory.Exists(root))
                    return;

                DateTime limit = DateTime.UtcNow.AddDays(-14);
                int removed = 0;
                foreach (string d in Directory.GetDirectories(root))
                {
                    try
                    {
                        if (Directory.GetLastWriteTimeUtc(d) < limit)
                        {
                            Directory.Delete(d, true);
                            removed++;
                        }
                    }
                    catch { }
                }
                if (removed > 0)
                    Log.Write("清理过期导入缓存 " + removed + " 个");
            }
            catch { }
        }

        private string TemplateDir
        {
            get
            {
                // 模板固定用 exe 同目录的 template，不再提供手动修改：
                // 相对路径跟着程序走，换机器/换目录都不会失效
                string def = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "template");
                if (Directory.Exists(def))
                    return def;

                return AppDomain.CurrentDomain.BaseDirectory;
            }
        }

        /// <summary>默认输出目录：exe 同目录下的 PKG（启动时就建好）。</summary>
        private static string DefaultOutDir
        {
            get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "PKG"); }
        }

        /// <summary>
        /// 把输入框里的内容变成可用的绝对路径：空 = 默认 PKG 目录；
        /// 相对路径按 exe 所在目录展开，避免用户写「PKG」时落到当前工作目录。
        /// </summary>
        private static string ResolveOutDir(string raw)
        {
            string t = (raw ?? "").Trim().Trim('"');
            if (t.Length == 0)
                t = DefaultOutDir;
            if (!Path.IsPathRooted(t))
                t = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, t);
            return t;
        }

        private string OrbisExe
        {
            get { return Path.Combine(TemplateDir, "orbis-pub-cmd.exe"); }
        }

        private void BuildUi()
        {
            // ---- 顶部工具条（改成两行：普通按钮 + 可编辑的路径框） ----
            Panel bar = BuildTopBar();

            // ---- 左：游戏列表 ----
            grid = new DataGridView();
            grid.Dock = DockStyle.Fill;
            grid.AllowUserToAddRows = false;
            grid.AllowUserToDeleteRows = false;
            grid.ReadOnly = true;
            grid.RowHeadersVisible = false;
            grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            grid.MultiSelect = true;
            grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
            grid.BackgroundColor = Color.White;
            grid.BorderStyle = BorderStyle.None;
            grid.AllowDrop = true;
            grid.DragEnter += GridDragEnter;
            grid.DragDrop += GridDragDrop;

            grid.Columns.Add(MakeCol("来源", 60, 8));
            grid.Columns.Add(MakeCol("标题", 300, 40));
            grid.Columns.Add(MakeCol("Title ID", 90, 12));
            grid.Columns.Add(MakeCol("类型", 60, 8));
            grid.Columns.Add(MakeCol("状态", 220, 22));

            // 右键：编辑/上传这个游戏的奖杯解锁顺序表
            ContextMenuStrip menu = new ContextMenuStrip();
            ToolStripMenuItem miOrder = new ToolStripMenuItem("解锁顺序表（编辑，打包时写入 pkg）");
            miOrder.Click += OnEditOrder;
            menu.Items.Add(miOrder);
            grid.ContextMenuStrip = menu;

            grid.CellMouseDown += OnGridRightClick;

            Label lblLeft = new Label();
            lblLeft.Text = "  游戏列表 —— 直接把 .pkg 拖进来（支持多个），或点「导入 PKG」";
            lblLeft.Dock = DockStyle.Top;
            lblLeft.Height = 26;
            lblLeft.TextAlign = ContentAlignment.MiddleLeft;
            lblLeft.BackColor = Color.FromArgb(245, 247, 250);

            Panel pnlLeft = new Panel();
            pnlLeft.Dock = DockStyle.Fill;
            pnlLeft.Controls.Add(grid);
            pnlLeft.Controls.Add(lblLeft);

            // ---- 右：输出文件 ----
            outputs = new ListView();
            outputs.Dock = DockStyle.Fill;
            outputs.View = View.Details;
            outputs.FullRowSelect = true;
            outputs.GridLines = false;
            outputs.Columns.Add("文件", 300);
            outputs.Columns.Add("大小", 80, HorizontalAlignment.Right);
            outputs.Columns.Add("时间", 90, HorizontalAlignment.Right);
            outputs.DoubleClick += delegate { OpenSelectedOutput(); };

            Label lblRight = new Label();
            lblRight.Text = "  输出文件（双击打开所在文件夹）";
            lblRight.Dock = DockStyle.Top;
            lblRight.Height = 26;
            lblRight.TextAlign = ContentAlignment.MiddleLeft;
            lblRight.BackColor = Color.FromArgb(245, 247, 250);

            Panel pnlRight = new Panel();
            pnlRight.Dock = DockStyle.Fill;
            pnlRight.Controls.Add(outputs);
            pnlRight.Controls.Add(lblRight);

            SplitContainer split = new SplitContainer();
            split.Dock = DockStyle.Fill;
            split.Orientation = Orientation.Vertical;
            split.SplitterDistance = 720;
            split.Panel1.Controls.Add(pnlLeft);
            split.Panel2.Controls.Add(pnlRight);

            // ---- 底部状态栏 ----
            StatusStrip status = new StatusStrip();
            lblStatus = new ToolStripStatusLabel("就绪");
            lblStatus.Spring = true;
            lblStatus.TextAlign = ContentAlignment.MiddleLeft;
            status.Items.Add(lblStatus);

            Controls.Add(split);
            Controls.Add(status);
            Controls.Add(bar);

            AllowDrop = true;
            DragEnter += GridDragEnter;
            DragDrop += GridDragDrop;
        }

        /// <summary>
        /// 顶部两条：第一行放动作（导入 / 清除 / 扫描 FTP），第二行放输出目录。
        /// 原来挤在一个 ToolStrip 里，窗口一窄后面的项就掉进溢出菜单，
        /// 输出路径框点不进去也改不动，所以改成普通控件 + 真按钮。
        /// </summary>
        private Panel BuildTopBar()
        {
            Panel bar = new Panel();
            bar.Dock = DockStyle.Top;
            bar.Height = 96;
            bar.BackColor = Color.FromArgb(247, 249, 252);
            bar.Padding = new Padding(12, 10, 12, 8);

            // ---- 第一行 ----
            FlowLayoutPanel row1 = new FlowLayoutPanel();
            row1.Dock = DockStyle.Top;
            row1.Height = 36;
            row1.WrapContents = false;
            row1.AutoScroll = false;

            Button btnImport = MakeButton("导入 PKG…");
            btnImport.Click += OnImportClick;

            Button btnClear = MakeButton("清除列表");
            btnClear.Click += delegate { ClearList(); };

            Button btnFtp = MakeButton("扫描 FTP");
            btnFtp.Click += OnScanFtpClick;

            // 「连接解锁」：跳到奖杯时间戳编辑器（直连 PS4 改 trophy_local.db，
            // 不打包 PKG；实测安全规则见 TrophyEditor 注释）
            Button btnTrophyEditor = MakeButton("连接解锁", true);
            btnTrophyEditor.Click += delegate
            {
                var editor = new TrophyEditor(txtHost.Text);
                editor.Show(this);
            };
            tips.SetToolTip(btnTrophyEditor, "直连 PS4 编辑奖杯解锁状态与时间戳（无需打包 PKG）");

            Label lblHost = MakeLabel("PS4 IP");
            txtHost = MakeText("192.168.1.10", 130);
            tips.SetToolTip(txtHost, "PS4 的 IP（GoldHEN FTP 端口 2121）");

            row1.Controls.Add(btnImport);
            row1.Controls.Add(btnClear);
            row1.Controls.Add(MakeSep());
            row1.Controls.Add(lblHost);
            row1.Controls.Add(txtHost);
            row1.Controls.Add(btnFtp);
            row1.Controls.Add(MakeSep());
            row1.Controls.Add(btnTrophyEditor);

            // ---- 第二行 ----
            TableLayoutPanel row2 = new TableLayoutPanel();
            row2.Dock = DockStyle.Top;
            row2.Height = 38;
            row2.ColumnCount = 4;
            row2.RowCount = 1;
            row2.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            row2.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            row2.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            row2.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

            Label lblOut = MakeLabel("输出目录");
            txtOut = MakeText(DefaultOutDir, 10);
            txtOut.Dock = DockStyle.Fill;
            txtOut.Margin = new Padding(0, 5, 8, 0);
            tips.SetToolTip(txtOut, "生成的 pkg 放在这里，默认是 exe 同目录下的 PKG 文件夹（可改）");

            Button btnOut = MakeButton("浏览..");
            btnOut.Click += OnPickOutClick;
            tips.SetToolTip(btnOut, "选择输出文件夹");

            btnBuild = MakeButton("生成 PKG", true);
            btnBuild.Click += OnBuildClick;

            row2.Controls.Add(lblOut, 0, 0);
            row2.Controls.Add(txtOut, 1, 0);
            row2.Controls.Add(btnOut, 2, 0);
            row2.Controls.Add(btnBuild, 3, 0);

            // Dock=Top 后加的在上，所以先加第二行
            bar.Controls.Add(row2);
            bar.Controls.Add(row1);
            return bar;
        }

        private static Button MakeButton(string text, bool primary = false)
        {
            Button b = new Button();
            b.Text = text;
            b.AutoSize = true;
            b.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            b.MinimumSize = new Size(0, 30);
            b.Padding = new Padding(14, 2, 14, 2);
            b.Margin = new Padding(0, 0, 8, 0);
            b.FlatStyle = FlatStyle.Flat;
            b.FlatAppearance.BorderSize = 1;
            b.UseVisualStyleBackColor = false;
            b.Cursor = Cursors.Hand;

            if (primary)
            {
                b.BackColor = Color.FromArgb(47, 111, 237);
                b.ForeColor = Color.White;
                b.Font = new Font("Microsoft YaHei UI", 9f, FontStyle.Bold);
                b.FlatAppearance.BorderColor = Color.FromArgb(36, 92, 205);
                b.FlatAppearance.MouseOverBackColor = Color.FromArgb(62, 126, 246);
                b.FlatAppearance.MouseDownBackColor = Color.FromArgb(33, 86, 194);
                b.Margin = new Padding(4, 0, 0, 0);
            }
            else
            {
                b.BackColor = Color.White;
                b.ForeColor = Color.FromArgb(32, 38, 48);
                b.FlatAppearance.BorderColor = Color.FromArgb(196, 202, 212);
                b.FlatAppearance.MouseOverBackColor = Color.FromArgb(236, 241, 249);
                b.FlatAppearance.MouseDownBackColor = Color.FromArgb(214, 224, 240);
            }

            return b;
        }

        private static Label MakeLabel(string text)
        {
            Label l = new Label();
            l.Text = text;
            l.AutoSize = true;
            l.ForeColor = Color.FromArgb(72, 80, 92);
            l.Margin = new Padding(0, 8, 7, 0);
            return l;
        }

        private static TextBox MakeText(string text, int width)
        {
            TextBox t = new TextBox();
            t.Text = text;
            t.Width = width;
            t.BorderStyle = BorderStyle.FixedSingle;
            t.Margin = new Padding(0, 5, 8, 0);
            t.Font = new Font("Microsoft YaHei UI", 9f);
            return t;
        }

        private static Panel MakeSep()
        {
            Panel p = new Panel();
            p.Width = 1;
            p.Height = 22;
            p.BackColor = Color.FromArgb(210, 216, 226);
            p.Margin = new Padding(6, 6, 14, 0);
            return p;
        }

        private void OnPickOutClick(object sender, EventArgs e)
        {
            using (FolderBrowserDialog dlg = new FolderBrowserDialog())
            {
                dlg.Description = "选择 pkg 输出目录";
                dlg.ShowNewFolderButton = true;
                string cur = ResolveOutDir(txtOut.Text);
                if (Directory.Exists(cur))
                    dlg.SelectedPath = cur;
                if (dlg.ShowDialog(this) == DialogResult.OK)
                    txtOut.Text = dlg.SelectedPath;
            }
        }

        private static DataGridViewTextBoxColumn MakeCol(string header, int width, int fill)
        {
            DataGridViewTextBoxColumn c = new DataGridViewTextBoxColumn();
            c.HeaderText = header;
            c.Width = width;
            c.FillWeight = fill;
            return c;
        }

        // ---------------- 拖拽 ----------------

        private void GridDragEnter(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
                e.Effect = DragDropEffects.Copy;
        }

        private async void GridDragDrop(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop))
                return;

            string[] paths = (string[])e.Data.GetData(DataFormats.FileDrop);
            await ImportAsync(paths);
        }

        // ---------------- 解锁顺序表 ----------------

        private void OnGridRightClick(object sender, DataGridViewCellMouseEventArgs e)
        {
            if (e.Button != MouseButtons.Right || e.RowIndex < 0)
                return;

            grid.ClearSelection();
            grid.Rows[e.RowIndex].Selected = true;
        }

        private void OnEditOrder(object sender, EventArgs e)
        {
            if (grid.SelectedRows.Count == 0)
            {
                Status("先选中一个游戏");
                return;
            }

            GameItem it = grid.SelectedRows[0].Tag as GameItem;
            if (it == null || string.IsNullOrEmpty(it.TitleId))
            {
                Status("这个条目没有 Title ID");
                return;
            }

            string dir = ResolveOutDir(txtOut.Text);
            Directory.CreateDirectory(dir);

            // 放在输出目录：打包时会自动写进 pkg 的 /app0/trophy_order.csv
            string local = Path.Combine(dir, "trophy_order_" + it.TitleId + ".csv");

            bool created = false;
            if (!File.Exists(local))
            {
                File.WriteAllText(local, OrderTemplate(it), new UTF8Encoding(false));
                created = true;
                Log.Write("已生成顺序表模板: " + local);
            }

            OpenInNotepad(local);

            if (created)
                Status("已生成顺序表，改完直接生成 PKG 就会带进包里");
            else
                Status("已打开现有顺序表（打 PKG 时会用这个文件）");
        }

        private static void OpenInNotepad(string file)
        {
            try
            {
                System.Diagnostics.Process.Start("notepad.exe", "\"" + file + "\"");
            }
            catch (Exception ex)
            {
                Log.Write("打开记事本失败: " + ex.Message);
            }
        }

        private static string OrderTemplate(GameItem it)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("# " + (it.Title ?? "") + "  " + it.TitleId + " 的奖杯解锁顺序");
            sb.AppendLine("# 一行一个：奖杯ID,延迟秒（延迟 = 解锁这个之后等多少秒做下一个）");
            sb.AppendLine("# 这个文件会被写进 pkg 的 /app0/trophy_order.csv，启动 Unlocker 就按它走。");
            sb.AppendLine("#");
            sb.AppendLine("# 全部注释掉 = 默认行为：除了白金以外全部解锁，0 号白金自动放最后。");
            sb.AppendLine("# 想自定义就去掉下面几行前面的 #，并按你的顺序填写；");
            sb.AppendLine("# 0 号是白金，提前解锁会出错，程序会自动把它挪到最末尾。");
            sb.AppendLine("#");
            sb.AppendLine("# 1,2");
            sb.AppendLine("# 2,2");
            sb.AppendLine("# 3,3");
            return sb.ToString();
        }

        // ---------------- 导入 ----------------

        private async void OnImportClick(object sender, EventArgs e)
        {
            using (OpenFileDialog dlg = new OpenFileDialog())
            {
                dlg.Title = "选择要导入的 pkg（可多选）";
                dlg.Filter = "PS4 PKG (*.pkg)|*.pkg|所有文件 (*.*)|*.*";
                dlg.Multiselect = true;
                if (dlg.ShowDialog() != DialogResult.OK)
                    return;
                await ImportAsync(dlg.FileNames);
            }
        }

        private async void OnScanFtpClick(object sender, EventArgs e)
        {
            await ScanFtpAsync();
        }

        private async void OnBuildClick(object sender, EventArgs e)
        {
            await BuildAsync();
        }

        private async Task ImportAsync(string[] paths)
        {
            List<string> pkgs = new List<string>();
            foreach (string p in paths)
            {
                if (p.EndsWith(".pkg", StringComparison.OrdinalIgnoreCase))
                    pkgs.Add(p);
            }

            if (pkgs.Count == 0)
            {
                if (paths.Length == 1)
                    MessageBox.Show("拖进来的不是 .pkg 文件。", "导入",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            int ok = 0, notGame = 0, bad = 0;
            string singleError = null;
            string notGameTitle = null;

            SetBusy(true, "读取 pkg…");

            foreach (string p in pkgs)
            {
                // 文件名里就有 Content ID / Title ID，先秒出一行，别让人干等
                GameItem quick = new GameItem();
                quick.Source = "PKG";
                quick.Origin = p;
                quick.TitleId = PkgName.TitleId(p) ?? "";
                quick.ContentId = PkgName.ContentId(p) ?? "";
                quick.AppVer = PkgName.AppVer(p) ?? "";
                quick.Title = PkgName.Label(p);
                quick.Kind = GameKind.Game;

                int row = AddRow(quick);
                grid.Rows[row].Cells[4].Value = "读取中…";

                Status("读取 " + Path.GetFileName(p) + " …");

                try
                {
                    // 和原版一样：Read_PKG 直接把四个文件从条目表里读出来，不解包
                    bool fast = await Task.Run(() => PkgFast.Fill(quick));

                    // 只有连条目都拿不到时才退回 orbis（正常情况下永远走不到这里）
                    if (!fast || !quick.FilesLoaded)
                    {
                        Log.Write("条目直读不完整，改用 orbis-pub-cmd 解包: " + p);
                        string path = p;
                        GameItem full = await Task.Run(() => PkgReader.FromPkg(path, TemplateDir, workRoot));

                        quick.Title = full.Title;
                        quick.TitleId = full.TitleId;
                        quick.ContentId = full.ContentId;
                        quick.Category = full.Category;
                        quick.AppVer = full.AppVer;
                        quick.Kind = full.Kind;
                        quick.Param = full.Param;
                        quick.Nptitle = full.Nptitle;
                        quick.Npbind = full.Npbind;
                        quick.Trophy = full.Trophy;
                        quick.CacheDir = full.CacheDir;
                    }

                    if (!quick.Usable)
                    {
                        notGame++;
                        notGameTitle = quick.Title;
                        Log.Write("非游戏跳过: " + quick.Title + " / " + quick.TitleId + " / CATEGORY=" + quick.Category);
                        items.Remove(quick);
                        grid.Rows.RemoveAt(row);
                        continue;
                    }

                    UpdateRow(row, quick);
                    ok++;
                }
                catch (Exception ex)
                {
                    bad++;
                    Log.Error("导入 " + Path.GetFileName(p), ex);
                    singleError = Path.GetFileName(p) + "\r\n\r\n" + ex.Message;
                    quick.Kind = GameKind.Unknown;
                    grid.Rows[row].Cells[4].Value = "读取失败（详见 pkgstudio.log）";
                    Status("导入失败 " + Path.GetFileName(p) + " : " + FirstLine(ex.Message));
                }
            }

            SetBusy(false, "导入完成：成功 " + ok + "，非游戏 " + notGame + "，失败 " + bad);

            // 单个拖拽给明确原因；批量只在下方报数量
            if (pkgs.Count == 1)
            {
                if (notGame == 1)
                {
                    MessageBox.Show("这不是游戏本体"
                        + (string.IsNullOrEmpty(notGameTitle) ? "" : "（" + notGameTitle + "）")
                        + "，可能是更新或 DLC，不能用来做奖杯包。",
                        "不是游戏", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                else if (bad == 1 && singleError != null)
                {
                    MessageBox.Show(singleError, "导入失败",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
        }

        private void ClearList()
        {
            if (items.Count == 0)
            {
                Status("列表已经是空的");
                return;
            }
            if (MessageBox.Show("清空游戏列表？已生成的 pkg 文件不会被删除。", "清除列表",
                    MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK)
                return;

            grid.Rows.Clear();
            items.Clear();
            Status("列表已清空");
        }

        private void ShowErrors(string title, List<string> errs)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("失败 " + errs.Count + " 个：");
            sb.AppendLine();

            int n = 0;
            foreach (string e in errs)
            {
                string text = e.Length > 900 ? e.Substring(0, 900) + "…" : e;
                sb.AppendLine("· " + text);
                sb.AppendLine();
                if (++n >= 5)
                {
                    sb.AppendLine("（还有 " + (errs.Count - n) + " 条，完整内容见 exe 同目录 pkgstudio.log）");
                    break;
                }
            }

            MessageBox.Show(sb.ToString(), title, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        private int AddRow(GameItem it)
        {
            items.Add(it);

            int i = grid.Rows.Add(
                it.Source,
                string.IsNullOrEmpty(it.Title) ? "(无标题)" : it.Title,
                it.TitleId,
                it.KindText,
                it.StatusText);

            DataGridViewRow row = grid.Rows[i];
            row.Tag = it;
            if (!it.Usable)
                row.DefaultCellStyle.ForeColor = Color.Firebrick;

            return i;
        }

        private void UpdateRow(int index, GameItem it)
        {
            if (index < 0 || index >= grid.Rows.Count)
                return;

            DataGridViewRow row = grid.Rows[index];
            row.Cells[0].Value = it.Source;
            row.Cells[1].Value = string.IsNullOrEmpty(it.Title) ? "(无标题)" : it.Title;
            row.Cells[2].Value = it.TitleId;
            row.Cells[3].Value = it.KindText;
            row.Cells[4].Value = it.StatusText;
            row.Tag = it;
            row.DefaultCellStyle.ForeColor = it.Usable ? Color.Black : Color.Firebrick;
        }

        // ---------------- FTP ----------------

        private async Task ScanFtpAsync()
        {
            string host = txtHost.Text.Trim();
            if (host.Length == 0)
            {
                MessageBox.Show("先填 PS4 的 IP", "扫描 FTP", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            SetBusy(true, "扫描 FTP " + host + " …");
            try
            {
                string h = host;
                List<GameItem> found = await Task.Run(() => FtpSource.Scan(h, Status));

                int added = 0;
                foreach (GameItem it in found)
                {
                    bool dup = false;
                    foreach (GameItem old in items)
                    {
                        if (string.Equals(old.TitleId, it.TitleId, StringComparison.OrdinalIgnoreCase))
                        { dup = true; break; }
                    }
                    if (dup) continue;
                    AddRow(it);
                    added++;
                }
                SetBusy(false, "FTP 扫描完成：发现 " + found.Count + " 个，新增 " + added + " 个");
            }
            catch (Exception ex)
            {
                SetBusy(false, "FTP 扫描失败：" + FirstLine(ex.Message));
            }
        }

        // ---------------- 打包 ----------------

        private async Task BuildAsync()
        {
            List<GameItem> todo = new List<GameItem>();
            foreach (DataGridViewRow row in grid.SelectedRows)
            {
                GameItem it = row.Tag as GameItem;
                if (it != null && !todo.Contains(it))
                    todo.Add(it);
            }
            if (todo.Count == 0)
            {
                foreach (GameItem it in items)
                    todo.Add(it);
            }
            if (todo.Count == 0)
            {
                MessageBox.Show("列表是空的，先导入 pkg 或扫描 FTP", "生成 PKG");
                return;
            }

            string outDir = ResolveOutDir(txtOut.Text);
            if (outDir.Length == 0)
            {
                MessageBox.Show("先设置输出路径", "生成 PKG");
                return;
            }
            try
            {
                Directory.CreateDirectory(outDir);
            }
            catch (Exception ex)
            {
                MessageBox.Show("输出目录建不出来：\r\n" + outDir + "\r\n\r\n" + ex.Message,
                    "生成 PKG", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            SetBusy(true, "开始打包…");
            int ok = 0, skip = 0, fail = 0;
            List<string> errs = new List<string>();

            Log.Write("==== 开始打包 ====");
            Log.Write("模板目录: " + TemplateDir);
            Log.Write("orbis: " + OrbisExe + "  存在=" + File.Exists(OrbisExe));
            Log.Write("输出目录: " + outDir);

            foreach (GameItem it in todo)
            {
                if (!it.Usable)
                {
                    skip++;
                    Status("跳过 " + it.Title + "：" + it.KindText + " 不可打包");
                    continue;
                }

                Status("打包 " + it.Title + " …");
                try
                {
                    GameItem cur = it;
                    string tpl = TemplateDir;
                    string od = outDir;
                    string wr = workRoot;

                    if (cur.Source == "FTP")
                        await Task.Run(() => FtpSource.EnsureFiles(cur, delegate (string s) { }));
                    else if (!cur.FilesLoaded)
                        await Task.Run(() => PkgReader.EnsureFiles(cur, tpl, wr, delegate (string s) { }));

                    string pkg = await Task.Run(() => PkgBuilder.Build(cur, tpl, od, wr,
                        delegate (string s) { }));

                    AddOutput(pkg);
                    Log.Write("打包成功: " + pkg);
                    ok++;
                }
                catch (Exception ex)
                {
                    fail++;
                    Log.Error("打包 " + it.Title + " (" + it.TitleId + ")", ex);
                    errs.Add(it.Title + " [" + it.TitleId + "]\r\n" + ex.Message);
                    Status("打包失败 " + it.Title + " : " + FirstLine(ex.Message));
                }
            }

            SetBusy(false, "完成：成功 " + ok + "，跳过 " + skip + "，失败 " + fail);
            if (errs.Count > 0)
                ShowErrors("打包失败", errs);
        }

        private void AddOutput(string path)
        {
            FileInfo fi = new FileInfo(path);
            ListViewItem lvi = new ListViewItem(fi.Name);
            lvi.SubItems.Add((fi.Length / 1024.0 / 1024.0).ToString("0.0") + " MB");
            lvi.SubItems.Add(fi.CreationTime.ToString("HH:mm:ss"));
            lvi.Tag = path;
            outputs.Items.Insert(0, lvi);
        }

        private void OpenSelectedOutput()
        {
            if (outputs.SelectedItems.Count == 0)
                return;
            string p = outputs.SelectedItems[0].Tag as string;
            if (p != null && File.Exists(p))
                System.Diagnostics.Process.Start("explorer.exe", "/select,\"" + p + "\"");
        }

        // ---------------- 杂项 ----------------

        private void SetBusy(bool busy, string message)
        {
            btnBuild.Enabled = !busy;
            Cursor = busy ? Cursors.WaitCursor : Cursors.Default;
            Status(message);
        }

        private void Status(string message)
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action<string>(Status), message);
                return;
            }
            lblStatus.Text = "  " + message;
        }

        private static string FirstLine(string s)
        {
            if (string.IsNullOrEmpty(s))
                return "";
            int i = s.IndexOfAny(new char[] { '\r', '\n' });
            return i < 0 ? s : s.Substring(0, i);
        }
    }
}
