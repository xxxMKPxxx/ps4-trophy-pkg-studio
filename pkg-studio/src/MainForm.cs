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
        private ToolStripTextBox txtHost;
        private ToolStripTextBox txtOut;
        private ToolStripTextBox txtTpl;
        private ToolStripStatusLabel lblStatus;
        private ToolStripButton btnBuild;

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
                string t = txtTpl.Text.Trim();
                if (t.Length > 0 && Directory.Exists(t))
                    return t;

                string def = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "template");
                if (Directory.Exists(def))
                    return def;

                return AppDomain.CurrentDomain.BaseDirectory;
            }
        }

        private string OrbisExe
        {
            get { return Path.Combine(TemplateDir, "orbis-pub-cmd.exe"); }
        }

        private void BuildUi()
        {
            // ---- 顶部工具条 ----
            ToolStrip bar = new ToolStrip();
            bar.GripStyle = ToolStripGripStyle.Hidden;
            bar.Padding = new Padding(6, 4, 6, 4);

            ToolStripButton btnImport = new ToolStripButton("导入 PKG...");
            btnImport.Click += OnImportClick;

            ToolStripButton btnClear = new ToolStripButton("清除列表");
            btnClear.Click += delegate { ClearList(); };

            ToolStripButton btnFtp = new ToolStripButton("扫描 FTP");
            btnFtp.Click += OnScanFtpClick;

            txtHost = new ToolStripTextBox();
            txtHost.Width = 120;
            txtHost.Text = "192.168.1.10";
            txtHost.ToolTipText = "PS4 的 IP（GoldHEN FTP 端口 2121）";

            ToolStripLabel lblOut = new ToolStripLabel("输出：");
            txtOut = new ToolStripTextBox();
            txtOut.Width = 240;
            txtOut.Text = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "PKG_Out");
            ToolStripButton btnOut = new ToolStripButton("...");
            btnOut.Click += delegate { PickFolder(txtOut); };

            ToolStripLabel lblTpl = new ToolStripLabel("模板：");
            txtTpl = new ToolStripTextBox();
            txtTpl.Width = 200;
            txtTpl.Text = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "template");
            ToolStripButton btnTpl = new ToolStripButton("...");
            btnTpl.Click += delegate { PickFolder(txtTpl); };

            btnBuild = new ToolStripButton("生成 PKG");
            btnBuild.Font = new Font(btnBuild.Font, FontStyle.Bold);
            btnBuild.Click += OnBuildClick;

            bar.Items.AddRange(new ToolStripItem[]
            {
                btnImport, btnClear, new ToolStripSeparator(),
                btnFtp, txtHost, new ToolStripSeparator(),
                lblOut, txtOut, btnOut, new ToolStripSeparator(),
                lblTpl, txtTpl, btnTpl, new ToolStripSeparator(),
                btnBuild
            });

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
            ToolStripMenuItem miOrder = new ToolStripMenuItem("解锁顺序表（编辑并上传）");
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

            string host = txtHost.Text.Trim();
            if (host.Length == 0)
            {
                MessageBox.Show("先在上方填 PS4 的 IP（GoldHEN FTP 端口 2121）。", "解锁顺序表",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            string dir = txtOut.Text.Trim();
            if (dir.Length == 0)
                dir = AppDomain.CurrentDomain.BaseDirectory;
            Directory.CreateDirectory(dir);

            string local = Path.Combine(dir, "trophy_order_" + it.TitleId + ".csv");
            string remote = "/data/GoldHEN/trophy_order_" + it.TitleId + ".csv";

            if (!File.Exists(local))
            {
                if (FtpSource.TryDownload(host, remote, local))
                {
                    Log.Write("已从 PS4 下载顺序表: " + remote);
                    Status("已下载顺序表，改完再右键上传");
                }
                else
                {
                    File.WriteAllText(local, OrderTemplate(it), new UTF8Encoding(false));
                    Log.Write("PS4 上没有顺序表，已生成模板: " + local);
                    Status("已生成模板；在游戏里按 □ 能得到按真实奖杯数排列的版本");
                }

                OpenInNotepad(local);
                return;
            }

            DialogResult r = MessageBox.Show(
                "本地已有：\r\n" + local + "\r\n\r\n是 = 上传到 PS4（覆盖远端）\r\n否 = 打开编辑",
                "解锁顺序表", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);

            if (r == DialogResult.Yes)
            {
                int lines = CountOrderLines(local);
                if (lines <= 0)
                {
                    MessageBox.Show("文件里没有有效的「奖杯ID,延迟」行。\r\n先在游戏里按 □ 生成一份再改。",
                        "解锁顺序表", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                if (FtpSource.Upload(host, remote, local))
                    Status("已上传顺序表：" + lines + " 条");
                else
                    Status("上传失败，详见 pkgstudio.log");
            }
            else if (r == DialogResult.No)
            {
                OpenInNotepad(local);
            }
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

        private static int CountOrderLines(string file)
        {
            int n = 0;
            foreach (string raw in File.ReadAllLines(file))
            {
                string s = raw.Trim();
                if (s.Length == 0 || s.StartsWith("#"))
                    continue;
                if (char.IsDigit(s[0]))
                    n++;
            }
            return n;
        }

        private static string OrderTemplate(GameItem it)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("# " + (it.Title ?? "") + "  " + it.TitleId + " 的奖杯解锁顺序");
            sb.AppendLine("# 一行一个：奖杯ID,延迟毫秒（延迟 = 解锁这个之后等多久做下一个）");
            sb.AppendLine("# 0 号通常就是白金，放最后；不想要的奖杯删掉那一行即可");
            sb.AppendLine("# 在游戏里按 □ 会按真实奖杯数量自动生成一份，那份的 id 更准");
            sb.AppendLine("1,1500");
            sb.AppendLine("2,1500");
            sb.AppendLine("0,2000");
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

            string outDir = txtOut.Text.Trim();
            if (outDir.Length == 0)
            {
                MessageBox.Show("先设置输出路径", "生成 PKG");
                return;
            }
            Directory.CreateDirectory(outDir);

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

        private void PickFolder(ToolStripTextBox box)
        {
            using (FolderBrowserDialog dlg = new FolderBrowserDialog())
            {
                if (Directory.Exists(box.Text))
                    dlg.SelectedPath = box.Text;
                if (dlg.ShowDialog() == DialogResult.OK)
                    box.Text = dlg.SelectedPath;
            }
        }

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
