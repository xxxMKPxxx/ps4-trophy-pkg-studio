using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
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
        private ToolStripLabel lblStatus;
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
            btnImport.Click += delegate { PickPkgs(); };

            ToolStripButton btnFtp = new ToolStripButton("扫描 FTP");
            btnFtp.Click += async delegate { await ScanFtpAsync(); };

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
            btnBuild.Click += async delegate { await BuildAsync(); };

            bar.Items.AddRange(new ToolStripItem[]
            {
                btnImport, new ToolStripSeparator(),
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

        // ---------------- 导入 ----------------

        private async void PickPkgs()
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

        private async Task ImportAsync(string[] paths)
        {
            int ok = 0, bad = 0;
            SetBusy(true, "导入中…");

            foreach (string p in paths)
            {
                if (!p.EndsWith(".pkg", StringComparison.OrdinalIgnoreCase))
                    continue;

                Status("导入 " + Path.GetFileName(p) + " …");
                try
                {
                    string path = p;
                    GameItem it = await Task.Run(() => PkgReader.FromPkg(path, OrbisExe, workRoot));
                    AddRow(it);
                    ok++;
                }
                catch (Exception ex)
                {
                    bad++;
                    Status("导入失败 " + Path.GetFileName(p) + " : " + FirstLine(ex.Message));
                }
            }

            SetBusy(false, "导入完成：成功 " + ok + " 个，失败 " + bad + " 个");
        }

        private void AddRow(GameItem it)
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

                    string pkg = await Task.Run(() => PkgBuilder.Build(cur, tpl, od, wr,
                        delegate (string s) { }));

                    AddOutput(pkg);
                    ok++;
                }
                catch (Exception ex)
                {
                    fail++;
                    Status("打包失败 " + it.Title + " : " + FirstLine(ex.Message));
                }
            }

            SetBusy(false, "完成：成功 " + ok + "，跳过 " + skip + "，失败 " + fail);
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
