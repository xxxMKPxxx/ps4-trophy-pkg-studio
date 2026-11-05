# 给 PS4_Tools 的 TrophyUnlocker 打补丁：让每个游戏用唯一的 Title ID
#
# 用法：
#   powershell -ExecutionPolicy Bypass -File apply-patch.ps1 -RepoPath D:\PS4_Tools
#
# 做三件事（都可重复执行，已打过就跳过）：
#   1. 把 TitleIdUtil.cs 拷进 TrophyUnlocker 目录，并登记到 csproj
#   2. Form1.cs：newpkgid 改为运行时计算
#   3. 备份原文件为 .bak

param(
    [Parameter(Mandatory = $true)][string]$RepoPath,

    # 可选：指向 bin\Release（里面带预编译的 PS4_Tools.dll / PARAM.SFO.dll）。
    # 传了就会把断掉的 ProjectReference 和作者本机绝对路径的 HintPath 一并修好，
    # 这样在没有 PS4_Tools 工程源码的情况下也能编译。
    [string]$ReferenceDir
)

$ErrorActionPreference = 'Stop'

function Write-Step($msg) { Write-Host "[*] $msg" -ForegroundColor Cyan }
function Write-Ok($msg)   { Write-Host "[+] $msg" -ForegroundColor Green }
function Write-Warn2($msg){ Write-Host "[!] $msg" -ForegroundColor Yellow }

# 用花括号配对替换整个 C# 方法，避免对方法体写正则（里面有嵌套大括号）
function Replace-CSharpMethod {
    param([string]$Source, [string]$Signature, [string]$NewText)

    $idx = $Source.IndexOf($Signature)
    if ($idx -lt 0) { throw "没找到方法：$Signature" }

    $braceStart = $Source.IndexOf('{', $idx)
    if ($braceStart -lt 0) { throw "方法体缺失：$Signature" }

    $depth = 0
    $end = -1
    for ($i = $braceStart; $i -lt $Source.Length; $i++) {
        $ch = $Source[$i]
        if ($ch -eq '{') { $depth++ }
        elseif ($ch -eq '}') {
            $depth--
            if ($depth -eq 0) { $end = $i; break }
        }
    }
    if ($end -lt 0) { throw "方法体括号不匹配：$Signature" }

    # 连同方法所在行开头的缩进一起替换，避免留下空行
    $lineStart = $Source.LastIndexOf("`n", $idx) + 1

    return $Source.Substring(0, $lineStart) + $NewText + $Source.Substring($end + 1)
}

# 在指定方法结束后插入新代码（用于加辅助方法，不用关心原方法体长什么样）
function Insert-AfterCSharpMethod {
    param([string]$Source, [string]$Signature, [string]$Text)

    $idx = $Source.IndexOf($Signature)
    if ($idx -lt 0) { throw "没找到方法：$Signature" }

    $braceStart = $Source.IndexOf('{', $idx)
    if ($braceStart -lt 0) { throw "方法体缺失：$Signature" }

    $depth = 0
    $end = -1
    for ($i = $braceStart; $i -lt $Source.Length; $i++) {
        $ch = $Source[$i]
        if ($ch -eq '{') { $depth++ }
        elseif ($ch -eq '}') {
            $depth--
            if ($depth -eq 0) { $end = $i; break }
        }
    }
    if ($end -lt 0) { throw "方法体括号不匹配：$Signature" }

    return $Source.Insert($end + 1, "`r`n" + $Text)
}

$projDir = Join-Path $RepoPath 'TrophyUnlocker'
$form    = Join-Path $projDir 'Form1.cs'
$csproj  = Join-Path $projDir 'TrophyUnlocker.csproj'
$utilSrc = Join-Path $PSScriptRoot 'TitleIdUtil.cs'
$utilDst = Join-Path $projDir 'TitleIdUtil.cs'

if (-not (Test-Path $form))   { throw "找不到 $form，-RepoPath 要指向 PS4_Tools 仓库根目录" }
if (-not (Test-Path $utilSrc)){ throw "找不到 $utilSrc，请把 TitleIdUtil.cs 和本脚本放在同一目录" }

# ---------- 1. 拷工具类 ----------
Write-Step '安装工具类'
$utilFiles = @('TitleIdUtil.cs', 'SfoUtil.cs')
foreach ($name in $utilFiles) {
    $s = Join-Path $PSScriptRoot $name
    if (-not (Test-Path $s)) { throw "找不到 $s，请把补丁包里的 .cs 和本脚本放在同一目录" }
    Copy-Item $s (Join-Path $projDir $name) -Force
    Write-Ok "已写入 $name"
}

# ---------- 2. 登记到 csproj ----------
if (Test-Path $csproj) {
    $xml = Get-Content $csproj -Raw -Encoding UTF8
    $add = @()
    foreach ($name in $utilFiles) {
        if ($xml -notmatch [regex]::Escape($name)) { $add += $name }
    }
    if ($add.Count -gt 0) {
        # 老式 csproj 用显式 Compile 项，直接在 </Project> 前加独立 ItemGroup。
        # 不去正则匹配已有的 <Compile Include="Form1.cs">，因为真项目里它带 <SubType> 子节点，
        # 是 <Compile ...>...</Compile> 而非自闭合，按行匹配必失败。
        if ($xml -match '</Project>') {
            $items = ($add | ForEach-Object { "    <Compile Include=`"$_`" />" }) -join "`r`n"
            $inject = "  <ItemGroup>`r`n$items`r`n  </ItemGroup>`r`n</Project>"
            $xml = $xml -replace '</Project>', $inject
            [System.IO.File]::WriteAllText($csproj, $xml, [System.Text.Encoding]::UTF8)
            Write-Ok ("已登记到 csproj: " + ($add -join ', '))
        } else {
            Write-Warn2 'csproj 里找不到 </Project>，请手动把 .cs 加进项目'
        }
    } else {
        Write-Ok 'csproj 里已有工具类，跳过'
    }
} else {
    Write-Warn2 "没找到 $csproj，如果是 SDK 风格项目（自动包含 .cs）就不用管"
}

# ---------- 3. 改 Form1.cs ----------
Write-Step '修改 Form1.cs'

$src = [System.IO.File]::ReadAllText($form, [System.Text.Encoding]::UTF8)

$needTitlePatch = ($src -notmatch 'TitleIdUtil\.ForGameNpTitle')
$needNamePatch  = ($src -notmatch 'char\.IsLetterOrDigit')

# --- 3.1 唯一 Title ID ---
if ($needTitlePatch) {

    # newpkgid 先声明、后赋值（sfo 在它后面才创建，不能直接引用）
    $before = $src
    $src = $src -replace 'string\s+newpkgid\s*=\s*"XDPX30000_00"\s*;',
                         'string newpkgid;   // 补丁：下面按游戏 Title ID 计算'

    if ($src -eq $before) {
        throw '没匹配到 string newpkgid = "XDPX30000_00"; —— 源码版本可能不一样，请手动确认'
    }

    # sfo 创建之后立刻赋值
    $before = $src
    $src = $src -replace '(var\s+sfo\s*=\s*new\s+Param_SFO\.PARAM_SFO\([^;]*\)\s*;)',
                         "`$1`r`n            newpkgid = TitleIdUtil.ForGameNpTitle(sfo.TitleID);   // 补丁：唯一 Title ID"

    if ($src -eq $before) {
        throw '没匹配到 var sfo = new Param_SFO.PARAM_SFO(...); —— 源码版本可能不一样，请手动确认'
    }
    Write-Ok 'Title ID 已改为按游戏计算'
} else {
    Write-Ok 'Title ID 补丁已存在，跳过'
}

# --- 3.2 文件名净化：保留非英文字符 ---
# 原实现只允许 0-9A-Za-z 和 . _ 空格，中日韩标题会被全部剥掉，
# 最终 File.Move 的目标变成 "\.pkg"，抛异常后被后台线程静默吞掉 —— 表现为该游戏被跳过。
if ($needNamePatch) {
    $newMethod = @'
        public static string RemoveSpecialCharacters(string str)
        {
            if (string.IsNullOrEmpty(str))
                return "unlocker";

            // 保留 Unicode 字母数字（中/日/韩/俄/阿拉伯文等），只剔除文件名非法字符
            StringBuilder sb = new StringBuilder(str.Length);
            foreach (char c in str)
            {
                if (char.IsLetterOrDigit(c) || c == ' ' || c == '-' || c == '_' || c == '.')
                    sb.Append(c);
                else if (char.IsWhiteSpace(c))
                    sb.Append(' ');
            }

            string s = sb.ToString().Trim().TrimEnd('.');
            if (s.Length == 0)
                s = "unlocker";
            if (s.Length > 100)
                s = s.Substring(0, 100).TrimEnd('.', ' ');

            // Windows 保留设备名，直接当文件名会失败
            string[] reserved = { "CON", "PRN", "AUX", "NUL",
                                  "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
                                  "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" };
            if (Array.IndexOf(reserved, s.ToUpperInvariant()) >= 0)
                s = "_" + s;

            return s;
        }
'@
    try {
        $src = Replace-CSharpMethod -Source $src `
            -Signature 'public static string RemoveSpecialCharacters(string str)' `
            -NewText $newMethod
        Write-Ok 'RemoveSpecialCharacters 已改为保留 Unicode'
    } catch {
        Write-Warn2 "文件名补丁跳过：$($_.Exception.Message)"
    }
} else {
    Write-Ok '文件名补丁已存在，跳过'
}

# --- 3.3 FTP 端口：1337 -> 2121 ---
# 1337 是老式独立 FTP payload（XVortex）的端口；GoldHEN 内置 FTP 是 2121。
# 只要没额外跑那个旧 payload，硬编码 1337 必然连不上。
$needFtpPortPatch = ($src -match ':1337/')
if ($needFtpPortPatch) {
    $n = ([regex]::Matches($src, ':1337/')).Count
    $src = $src -replace ':1337/', ':2121/'
    Write-Ok "FTP 端口 1337 -> 2121（$n 处）"
} else {
    Write-Ok 'FTP 端口已是 2121 或已改过，跳过'
}

# --- 3.4 目录列表解析 ---
# 原逻辑按单空格 Split，再要求字段数恰好 9/10 并取固定下标。
# 但 `Jan  1` 这种日期补位会产生空元素，字段数在 9 和 10 之间摇摆，
# 一不吻合整个目录就被静默跳过（catch 是空的），表现为"连上了但没游戏"。
# 改为：只要求 >= 9 个字段，目录名一律取最后一个字段。
$needFtpListPatch = ($src -match 'splitt\[8\]|splitt\[9\]')
if ($needFtpListPatch) {
    $c8 = ([regex]::Matches($src, 'splitt\[8\]')).Count
    $c9 = ([regex]::Matches($src, 'splitt\[9\]')).Count

    if ($c8 -ne 5 -or $c9 -ne 2) {
        Write-Warn2 "splitt[8]=$c8 处、splitt[9]=$c9 处，与预期 5/2 不符，跳过解析补丁"
    } else {
        $src = $src -replace 'splitt\[9\]', 'splitt[splitt.Length - 1]'
        $src = $src -replace 'splitt\[8\]', 'splitt[splitt.Length - 1]'
        # 放宽字段数的同时也把 . / .. 排除掉，否则它们会被当成一个"用户"或"游戏"去拼路径
        $src = $src -replace 'if \(splitt\.Length == 10\)//only do this when 10 items in array',
                             'if (splitt.Length >= 9 && splitt[splitt.Length - 1] != "." && splitt[splitt.Length - 1] != "..")   // 补丁：放宽字段数，跳过 . 和 ..'
        $src = $src -replace 'if \(splitt\.Length == 9\)//only do this when 10 items in array',
                             'if (splitt.Length >= 9 && splitt[splitt.Length - 1] != "." && splitt[splitt.Length - 1] != "..")   // 补丁：同上'
        Write-Ok 'FTP 目录列表解析已放宽（并跳过 . / ..）'
    }
} else {
    Write-Ok '目录列表解析补丁已存在，跳过'
}

# --- 3.5 DownloadFile 容错 ---
# 原实现没有任何 try/catch，且没有设超时。任何一个文件取不到（例如
# /user/home/<uid>/username.dat 在 14.00 上不存在），550 会穿过整个
# backgroundWorker1_DoWork，直接把"扫描游戏列表"整轮中断掉。
$needDownloadPatch = ($src -notmatch '补丁：FTP 取不到文件')
if ($needDownloadPatch) {
    $newDownload = @'
        private byte[] DownloadFile(string ftpSourceFilePath)
        {
            // 补丁：失败不抛异常（否则一次 550 会打断整轮扫描），
            //       并且重试 3 次 —— 14.00 的 HEN 不稳定，偶发超时很常见。
            for (int attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    FtpWebRequest request = (FtpWebRequest)WebRequest.Create(ftpSourceFilePath);
                    request.Credentials = new NetworkCredential("anonymous", "janeDoe@contoso.com");
                    request.Method = WebRequestMethods.Ftp.DownloadFile;
                    request.UsePassive = true;
                    request.UseBinary = true;
                    request.Timeout = 20000;
                    request.ReadWriteTimeout = 20000;

                    using (FtpWebResponse response = (FtpWebResponse)request.GetResponse())
                    using (Stream reader = response.GetResponseStream())
                    {
                        return ReadFully(reader);
                    }
                }
                catch
                {
                    if (attempt == 2)
                        return new byte[0];
                    System.Threading.Thread.Sleep(300);
                }
            }
            return new byte[0];
        }
'@
    try {
        $src = Replace-CSharpMethod -Source $src `
            -Signature 'private byte[] DownloadFile(string ftpSourceFilePath)' `
            -NewText $newDownload
        Write-Ok 'DownloadFile 已改为容错 + 重试 3 次 + 20 秒超时'
    } catch {
        Write-Warn2 "DownloadFile 补丁跳过：$($_.Exception.Message)"
    }
} else {
    Write-Ok 'DownloadFile 补丁已存在，跳过'
}

# --- 3.55 先注入 LogLine 方法（后面几处日志都要用它，必须先存在） ---
$needLogMethod = ($src -notmatch '补丁：扫描日志')
if ($needLogMethod) {
    $logMethod = @'
        // 补丁：扫描日志，写到 exe 同目录的 trophylog.txt
        public static void LogLine(string s)
        {
            try
            {
                System.IO.File.AppendAllText(
                    System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "trophylog.txt"),
                    DateTime.Now.ToString("HH:mm:ss") + "  " + s + Environment.NewLine);
            }
            catch { }
        }
'@
    try {
        $src = Insert-AfterCSharpMethod -Source $src -Signature 'public static string AppCommonPath()' -Text $logMethod
        Write-Ok '已注入 LogLine 方法'
    } catch {
        Write-Warn2 "注入 LogLine 失败：$($_.Exception.Message)"
        $needLogMethod = $false
    }
} else {
    Write-Ok 'LogLine 已存在，跳过'
}

# --- 3.56 重写 backgroundWorker1_DoWork：双目录扫描 + 逐条日志 ---
# 原来只扫 /system_data/priv/appmeta/ 一个目录，且要求 param.sfo、nptitle.dat、
# npbind.dat、TROPHY.TRP 四个文件全部取到、两次解析全部成功，才 gamelist.Add()。
# 任何一步失败那个游戏就无声消失（catch 是空的）—— 这就是"能连上但列表不全"。
# 现在：三个候选目录都扫、按 TitleId 去重、每一条都写进 trophylog.txt。
$hasDoWork = $src.Contains('private void backgroundWorker1_DoWork(object sender, DoWorkEventArgs e)')
$needDoWorkPatch = ($src -notmatch '补丁：扫描两个 appmeta 目录') -and $hasDoWork
if (-not $hasDoWork) {
    Write-Warn2 '没找到 backgroundWorker1_DoWork，跳过扫描重写（不影响其他补丁）'
}
if ($needDoWorkPatch) {
    $newDoWork = @'
        // 补丁：扫描两个 appmeta 目录，逐条写日志
        private void backgroundWorker1_DoWork(object sender, DoWorkEventArgs e)
        {
            try
            {
                gamelist = new List<GameInfo>();

                string host = textBox1.Text.Trim();
                string root = "ftp://" + host + ":2121/";
                LogLine("=== 主机 " + host + " 开始连接 ===");
                UpdateProgress("Connecting " + host);

                // 不同固件、不同安装位置（内置盘 / 外置盘 / USB）下，appmeta 在不同的挂载点。
                // 原来只扫第一项 —— 装在 /mnt/ext0 上的游戏（日志里出现过 ext0 挂载）全都看不到，
                // 这就是"列表缺游戏、连日志里都没有"的直接原因。
                string[] dirs = new string[]
                {
                    "system_data/priv/appmeta/",
                    "user/appmeta/",
                    "mnt/ext0/user/appmeta/",
                    "mnt/ext1/user/appmeta/",
                    "mnt/usb0/user/appmeta/",
                    "mnt/usb1/user/appmeta/",
                    "appmeta/"
                };

                int total = 0, added = 0, dup = 0;
                System.Collections.Generic.HashSet<string> seen =
                    new System.Collections.Generic.HashSet<string>();

                foreach (string dir in dirs)
                {
                    string url = root + dir;
                    int entries = 0, ok = 0, raw = 0;

                    try
                    {
                        LogLine("=== 开始扫描 appmeta === " + dir);

                        FtpWebRequest req = (FtpWebRequest)WebRequest.Create(url);
                        req.Credentials = new NetworkCredential("anonymous", "janeDoe@contoso.com");
                        req.Method = WebRequestMethods.Ftp.ListDirectoryDetails;
                        req.UsePassive = true;
                        req.Timeout = 20000;

                        using (FtpWebResponse resp = (FtpWebResponse)req.GetResponse())
                        using (StreamReader sr = new StreamReader(resp.GetResponseStream()))
                        {
                            string line;
                            while ((line = sr.ReadLine()) != null)
                            {
                                raw++;
                                if (!LooksLikeDir(line)) continue;
                                string name = LastField(line);
                                if (name.Length == 0 || name == "." || name == "..") continue;

                                entries++;
                                total++;
                                LogLine("列表 " + dir + " -> " + name);

                                try
                                {
                                    GameInfo info = new GameInfo();
                                    UpdateProgress("Game found " + name);

                                    byte[] paramsfo = DownloadFile(url + name + "/param.sfo");
                                    if (paramsfo.Length == 0)
                                        throw new Exception("param.sfo 取不到");

                                    info.param = paramsfo;
                                    var sfofile = PS4_Tools.PKG.SceneRelated.PARAM_SFO.Get_Param_SFO(info.param);
                                    info.TitleId = sfofile.TitleID;
                                    info.Title = sfofile.Title;
                                    info.ContentID = sfofile.ContentID;

                                    if (string.IsNullOrEmpty(info.TitleId))
                                        throw new Exception("param.sfo 里没有 TITLE_ID");

                                    if (seen.Contains(info.TitleId))
                                    {
                                        dup++;
                                        LogLine("重复跳过 " + info.TitleId + " (" + name + ")");
                                        continue;
                                    }

                                    info.nptitle = DownloadFile(url + name + "/nptitle.dat");
                                    info.npbind = DownloadFile(url + name + "/npbind.dat");
                                    if (info.npbind.Length == 0)
                                        throw new Exception("npbind.dat 取不到");

                                    var bindinfo = new PS4_Tools.PKG.SceneRelated.NP_Bind(info.npbind);
                                    string tmpurl = root + "user/trophy/conf/" + bindinfo.Nptitle + "/";
                                    info.TrophyFile = DownloadFile(tmpurl + "TROPHY.TRP");
                                    if (info.TrophyFile.Length == 0)
                                        throw new Exception("TROPHY.TRP 取不到 (npcommid=" + bindinfo.Nptitle + ")");

                                    seen.Add(info.TitleId);
                                    gamelist.Add(info);
                                    added++;
                                    ok++;
                                    LogLine("OK   " + info.Title + "  " + info.TitleId);
                                }
                                catch (Exception ex)
                                {
                                    // 补丁：记录跳过原因，不再静默丢弃
                                    UpdateProgress("Skip " + name + " : " + ex.Message);
                                    LogLine("跳过 " + name + " : " + ex.Message);
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        LogLine("目录不可用 " + dir + " : " + ex.Message);
                    }

                    LogLine("--- " + dir + " 原始行=" + raw + " 目录行=" + entries + " 成功=" + ok + " ---");
                }

                LogLine("=== 完成 条目=" + total + " 加入=" + added + " 重复=" + dup + " 跳过=" + (total - added - dup) + " ===");
                UpdateProgress("Found " + added + " games, skipped " + (total - added - dup) + " (see trophylog.txt)");
            }
            catch (Exception ex)
            {
                LogLine("致命错误 : " + ex.ToString());
                MessageBox.Show("Error " + ex.Message, "Error while loading game data");
            }
        }
'@
    $src = Replace-CSharpMethod -Source $src `
        -Signature 'private void backgroundWorker1_DoWork(object sender, DoWorkEventArgs e)' `
        -NewText $newDoWork

    # 两个解析辅助方法：ls -l 和 MLSD 两种列表格式都认
    $helpers = @'
        // 补丁：只认目录行
        private static bool LooksLikeDir(string line)
        {
            if (string.IsNullOrEmpty(line)) return false;
            string t = line.Trim();
            if (t.Length == 0) return false;
            if (t[0] == 'd') return true;
            if (t.StartsWith("type=dir", StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        // 补丁：目录名一律取最后一个字段（ls 日期会补位，固定下标会错）
        private static string LastField(string line)
        {
            string[] parts = line.Split(new char[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return "";
            return parts[parts.Length - 1].Trim();
        }
'@
    try {
        $src = Insert-AfterCSharpMethod -Source $src -Signature 'public static void LogLine(string s)' -Text $helpers
    } catch {
        Write-Warn2 "辅助方法注入失败：$($_.Exception.Message)"
    }

    Write-Ok '已重写 DoWork：三目录扫描、按 TitleId 去重、逐条写日志'
} else {
    Write-Ok 'DoWork 已是多目录版本，跳过'
}

# --- 3.57 扫描完成回调：先清空再填充 ---
# 原实现只有 comboBox1.Items.Add()，从不 Clear。每点一次连接就再累加一遍，
# 列表里同一个游戏重复 N 次。顺便按标题去重（同名不同区服的条目会让
# comboBox1_SelectedIndexChanged 里的 gamelist.Single(...) 抛异常）。
$needUiPatch = ($src -notmatch '补丁：每次扫描先清空')
if ($needUiPatch) {
    $newCompleted = @'
        private void backgroundWorker1_RunWorkerCompleted(object sender, RunWorkerCompletedEventArgs e)
        {
            // 补丁：每次扫描先清空，否则重复扫描会一直累加
            comboBox1.Items.Clear();

            System.Collections.Generic.HashSet<string> titles =
                new System.Collections.Generic.HashSet<string>();
            for (int i = 0; i < gamelist.Count; i++)
            {
                if (!titles.Add(gamelist[i].Title)) continue;
                comboBox1.Items.Add(gamelist[i].Title);
            }
            LogLine("界面加入 " + comboBox1.Items.Count + " 条（gamelist=" + gamelist.Count + "）");

            if (gamelist.Count != 0)
            {
                comboBox1.SelectedIndex = 0;
            }

            pnlProgress.Visible = false;
        }
'@
    try {
        $src = Replace-CSharpMethod -Source $src `
            -Signature 'private void backgroundWorker1_RunWorkerCompleted(object sender, RunWorkerCompletedEventArgs e)' `
            -NewText $newCompleted
        Write-Ok '列表回调已改为"先清空 + 按标题去重"'
    } catch {
        Write-Warn2 "列表回调补丁跳过：$($_.Exception.Message)"
    }

    # Single() 在有重名时抛 InvalidOperationException，换成 FirstOrDefault()
    $before = $src
    $src = $src -replace 'gamelist\.Single\(', 'gamelist.FirstOrDefault('
    if ($src -ne $before) { Write-Ok 'gamelist.Single -> FirstOrDefault（避免重名崩溃）' }
} else {
    Write-Ok '列表回调补丁已存在，跳过'
}

# --- 3.58 param.sfo 的 TITLE 按 UTF-8 写 ---
# PS4 的 param.sfo 里字符串是 UTF-8，但 PS4_Tools 的 PARAM_SFO.SaveSFO()
# 用系统 ANSI 代码页写（中文 Windows 上是 GBK）。于是 "Unlocker for 龍が如く"
# 写回去就是乱码，XMB 里的应用名、pkg 文件名一起坏。
# 这里用 SfoUtil 按 UTF-8 重写 TITLE（写完回读校验，失败就保留原文件）。
$needSfoTitlePatch = ($src -notmatch '补丁：自己按 UTF-8 解原始')
if ($needSfoTitlePatch) {
    $anchor = '    //save the title as Unlocker For'
    $before = $src
    $inject = "    // 补丁：自己按 UTF-8 解原始 param.sfo 的标题（sfo.Title 是库按 ANSI 解出来的）`r`n" +
              "    string realTitle = SfoUtil.GetString(result.param, `"TITLE`");`r`n" +
              "    if (string.IsNullOrEmpty(realTitle)) realTitle = sfo.Title;`r`n`r`n" +
              $anchor
    $src = $src.Replace($anchor, $inject)
    if ($src -eq $before) { Write-Warn2 '没找到 "//save the title as Unlocker For"，跳过标题编码补丁' }

    $before = $src
    $src = $src.Replace('tempitem.Value = "Unlocker for " + sfo.Title;',
                        'tempitem.Value = "Unlocker for " + realTitle;')
    if ($src -eq $before) { Write-Warn2 '没找到 TITLE 赋值行' }

    $saveCall = 'sfo.SaveSFO(sfo, AppCommonPath() + @"CONTENTS\sce_sys\param.sfo");'
    $before = $src
    $src = $src.Replace($saveCall, $saveCall + "`r`n" +
        "    // 补丁：SaveSFO 按 ANSI 写字符串，中/日文标题会乱码 —— 用 UTF-8 再写一次 TITLE`r`n" +
        "    try`r`n" +
        "    {`r`n" +
        "        SfoUtil.SetString(AppCommonPath() + @`"CONTENTS\sce_sys\param.sfo`", `"TITLE`", `"Unlocker for `" + realTitle);`r`n" +
        "        LogLine(`"标题按 UTF-8 写入: Unlocker for `" + realTitle);`r`n" +
        "    }`r`n" +
        "    catch (Exception ex)`r`n" +
        "    {`r`n" +
        "        LogLine(`"标题修正失败（保留原样）: `" + ex.Message);`r`n" +
        "    }")
    if ($src -eq $before) { Write-Warn2 '没找到 sfo.SaveSFO 调用行' }

    # pkg 文件名也跟着用正确的标题，否则文件名同样是乱码
    $before = $src
    $src = $src.Replace('RemoveSpecialCharacters(sfo.Title)', 'RemoveSpecialCharacters(realTitle)')
    if ($src -eq $before) { Write-Warn2 '没找到文件名里的 sfo.Title' }

    Write-Ok '标题改为按 UTF-8 写入（含 pkg 文件名）'
} else {
    Write-Ok '标题编码补丁已存在，跳过'
}

# --- 3.585 pkg 文件名改回纯 ASCII ---
# 显示名放在 param.sfo 的 TITLE 里（UTF-8，XMB 正常），文件名则要经过
# Windows 文件系统 → exFAT U 盘 → PS4 安装器。中日文文件名在最后一步可能被拒，
# 表现就是"无法安装"却给不出原因。所以文件名一律 ASCII，并附 Title ID 保证唯一。
$needAsciiName = ($src -match 'RemoveSpecialCharacters\(realTitle\)')
if ($needAsciiName) {
    $src = $src.Replace('RemoveSpecialCharacters(realTitle)',
                        'TitleIdUtil.SafePkgName(realTitle, sfo.TitleID)')
    Write-Ok 'pkg 文件名改为纯 ASCII（标题 + TitleID）'
} else {
    Write-Ok 'pkg 文件名已是 ASCII 方案，跳过'
}

# --- 3.59 CONTENT_ID 自检 ---
# PS4 装不上 pkg 最常见的原因之一：CONTENT_ID 的中间段和 TITLE_ID 不一致。
# 原代码用 tempitem.Value.Replace(sfo.TitleID, ...)，而 sfo.TitleID 在循环里
# 可能已经被改成新值（取决于库是否缓存），Replace 就成了空操作 —— 结果
# TITLE_ID=XDPX12345 而 CONTENT_ID 还是 CUSA04893，安装直接被拒。
# 这里不猜，打包前把两个值读出来核对，不一致就按 TITLE_ID 重建 CONTENT_ID。
$needCidBlock = ($src -notmatch '补丁：CONTENT_ID 自检')
$needPkgBlock = ($src -notmatch 'pkg 未生成')
$needCidCheck = ($needCidBlock -or $needPkgBlock)
if ($needCidCheck) {
    $cidBlock = @'
    // 补丁：CONTENT_ID 自检 —— 中间段必须等于 TITLE_ID，否则 PS4 会拒绝安装
    try
    {
        string sfoPath = AppCommonPath() + @"CONTENTS\sce_sys\param.sfo";
        byte[] sfoBytes = File.ReadAllBytes(sfoPath);
        string tidNow = SfoUtil.GetString(sfoBytes, "TITLE_ID");
        string cidNow = SfoUtil.GetString(sfoBytes, "CONTENT_ID");
        LogLine("param.sfo 自检: TITLE_ID=" + tidNow + "  CONTENT_ID=" + cidNow);

        if (!string.IsNullOrEmpty(tidNow) && !string.IsNullOrEmpty(cidNow))
        {
            string[] seg = cidNow.Split('-');
            if (seg.Length >= 3 && seg[1].Replace("_00", "") != tidNow)
            {
                string fixedCid = seg[0] + "-" + tidNow + "_00-" + seg[2];
                if (SfoUtil.CidFixEnabled)
                {
                    SfoUtil.SetString(sfoPath, "CONTENT_ID", fixedCid);
                    sfo = new Param_SFO.PARAM_SFO(sfoPath);
                    LogLine("CONTENT_ID 与 TITLE_ID 不一致，已重建为 " + fixedCid);
                }
                else
                {
                    // 默认只记录不改：原版工具靠 Replace 改 CONTENT_ID，
                    // 那条路已经验证能装。多改一次反而可能破坏格式。
                    LogLine("CONTENT_ID 与 TITLE_ID 不一致（默认不改；需要时 unlocker.ini 写 cidfix=1）: 建议值 " + fixedCid);
                }
            }
        }
    }
    catch (Exception ex)
    {
        LogLine("param.sfo 自检失败: " + ex.Message);
    }

'@
    $anchorGp4 = '    UpdateProgress("Creating GP4 Project");'
    if ($needCidBlock) {
    $before = $src
    $src = $src.Replace($anchorGp4, $cidBlock + $anchorGp4)
    if ($src -eq $before) { Write-Warn2 '没找到 "Creating GP4 Project"，CONTENT_ID 自检未插入' }
    }

    $pkgBlock = @'
    // 补丁：确认 orbis-pub-cmd 真的产出了 pkg（失败时这里能看出来）
    try
    {
        string builtPkg = saveFileDialog1.SelectedPath + @"\" + sfo.ContentID + "-A" + sfo.APP_VER.Replace(".", "") + "-V" + version.Replace(".", "") + ".pkg";
        if (File.Exists(builtPkg))
            LogLine("pkg 已生成: " + builtPkg + "  (" + new FileInfo(builtPkg).Length + " 字节)");
        else
            LogLine("pkg 未生成，orbis-pub-cmd 可能失败: " + builtPkg);
    }
    catch (Exception ex)
    {
        LogLine("检查 pkg 失败: " + ex.Message);
    }

'@
    $anchorRename = '    UpdateProgress("Renaming PKG");'
    if ($needPkgBlock) {
    $before = $src
    $src = $src.Replace($anchorRename, $anchorRename + "`r`n" + $pkgBlock)
    if ($src -eq $before) { Write-Warn2 '没找到 "Renaming PKG"，pkg 存在性检查未插入' }
    }

    Write-Ok '已加入 CONTENT_ID 自检与 pkg 产出检查'
} else {
    Write-Ok 'CONTENT_ID 自检已存在，跳过'
}

# --- 3.6 让"跳过"可见 ---
# Form1.cs 里有 5 处空的 catch (Exception ex) { }，只有 appmeta 那个循环里的
# 能访问 splitt。所以只替换**第一处**，而且不引用 splitt（否则 CS0103）。
$needSkipLogPatch = ($src -notmatch '补丁：记录跳过原因')
if ($needSkipLogPatch) {
    # 关键：不能取"第一个"空 catch —— 文件里有 5 个，只有 appmeta 那个能访问 splitt。
    # 先用 appmeta 独有的 "!= \"..\"" 判断定位（它是最后一个），再找它后面的第一个 catch。
    $rx     = [regex]'(?s)catch \(Exception ex\)\s*\{\s*\}'
    $anchor = 'splitt[splitt.Length - 1] != ".."'
    $ai     = $src.LastIndexOf($anchor)

    if ($ai -lt 0) {
        Write-Warn2 '找不到 appmeta 的 ".." 判断，跳过原因补丁未应用'
    } else {
        $cm = $rx.Match($src.Substring($ai))
        if (-not $cm.Success) {
            Write-Warn2 '该判断之后没有空 catch，跳过原因补丁未应用'
        } else {
            if ($needLogMethod) {
                $repl = "catch (Exception ex)`r`n                {`r`n                    UpdateProgress(`"补丁：记录跳过原因 `" + ex.Message);`r`n                    LogLine(`"跳过 : `" + ex.Message);`r`n                }"
            } else {
                $repl = "catch (Exception ex)`r`n                {`r`n                    UpdateProgress(`"补丁：记录跳过原因 `" + ex.Message);`r`n                }"
            }
            $start = $ai + $cm.Index
            $src = $src.Substring(0, $start) + $repl + $src.Substring($start + $cm.Length)
            Write-Ok '被跳过的游戏会记录原因（已定位到 appmeta 的那个 catch）'
        }
    }
} else {
    Write-Ok '跳过原因提示已存在，跳过'
}

# --- 3.7 扫描日志文件 ---
# 进度条刷得太快看不清，落一份文件到 exe 同目录。
# 注意：LogLine 方法由 3.55 注入，这里只负责插入调用点。
$needLogPatch = ($src -notmatch '=== 开始扫描 appmeta ===')
if ($needLogPatch) {
    # 扫描起点打个标记
    $before = $src
    $src = $src -replace '(url = "ftp://" \+ textBox1\.Text \+ ":2121/system_data/priv/appmeta/";)',
        "LogLine(`"=== 开始扫描 appmeta ===`");`r`n            `$1"
    if ($src -eq $before) { Write-Warn2 '没找到 appmeta 的 url 赋值行，日志少了起点标记' }

    # 记录每个成功解析的游戏
    $before = $src
    $src = $src -replace 'info\.ContentID = sfofile\.ContentID;',
        "info.ContentID = sfofile.ContentID;`r`n                    LogLine(`"OK   `" + info.Title + `"  `" + info.TitleId);"
    if ($src -eq $before) { Write-Warn2 '没找到 info.ContentID 赋值行，日志少了成功记录' }

    Write-Ok '扫描结果会写入 trophylog.txt'
} else {
    Write-Ok '扫描日志调用点已存在，跳过'
}

# ---------- 4. 修正引用（可选） ----------
# 原 csproj 有两处无法在干净环境编译：
#   1) PARAM.SFO 的 HintPath 指向作者的 ..\..\..\..\GitHub\PS3Tools\...
#   2) ProjectReference 指向仓库里根本不存在的 ..\PS4_Tools\PS4_Tools.csproj
# 拿 bin\Release 里的预编译 DLL 顶掉这两个引用即可。
if ($ReferenceDir) {
    Write-Step "修正 csproj 引用 -> $ReferenceDir"

    $ps4ToolsDll = Join-Path $ReferenceDir 'PS4_Tools.dll'
    $paramSfoDll = Join-Path $ReferenceDir 'PARAM.SFO.dll'
    if (-not (Test-Path $ps4ToolsDll)) { throw "找不到 $ps4ToolsDll" }
    if (-not (Test-Path $paramSfoDll)) { throw "找不到 $paramSfoDll" }

    [xml]$doc = Get-Content $csproj -Raw -Encoding UTF8
    $msbNs = 'http://schemas.microsoft.com/developer/msbuild/2003'
    $ns = New-Object System.Xml.XmlNamespaceManager($doc.NameTable)
    $ns.AddNamespace('m', $msbNs)

    $ref = $doc.SelectSingleNode("//m:Reference[@Include='PARAM.SFO']", $ns)
    if ($ref) {
        $hp = $ref.SelectSingleNode('m:HintPath', $ns)
        if (-not $hp) {
            # 必须带命名空间，否则会写成 xmlns=""，MSBuild 直接忽略该节点
            $hp = $doc.CreateElement('HintPath', $msbNs)
            [void]$ref.AppendChild($hp)
        }
        $hp.InnerText = $paramSfoDll
        Write-Ok 'PARAM.SFO 引用已指向本地 DLL'
    } else {
        Write-Warn2 '没找到 PARAM.SFO 引用节点'
    }

    $pr = $doc.SelectSingleNode('//m:ProjectReference', $ns)
    if ($pr) {
        $node = $doc.CreateElement('Reference', $msbNs)
        $node.SetAttribute('Include', 'PS4_Tools')
        $hp = $doc.CreateElement('HintPath', $msbNs)
        $hp.InnerText = $ps4ToolsDll
        [void]$node.AppendChild($hp)
        [void]$pr.ParentNode.ReplaceChild($node, $pr)
        Write-Ok 'ProjectReference 已替换为对 PS4_Tools.dll 的引用'
    } else {
        Write-Ok '没有 ProjectReference，跳过'
    }

    $doc.Save($csproj)
}

# ---------- 5. 备份并写回 ----------
if ($needTitlePatch -or $needNamePatch -or $needFtpPortPatch -or $needFtpListPatch -or $needDownloadPatch -or $needSkipLogPatch -or $needLogPatch -or $needLogMethod -or $needDoWorkPatch -or $needUiPatch -or $needSfoTitlePatch -or $needCidCheck -or $needAsciiName) {
    $bak = "$form.bak"
    if (-not (Test-Path $bak)) {
        Copy-Item $form $bak
        Write-Ok "已备份 $bak"
    }

    [System.IO.File]::WriteAllText($form, $src, [System.Text.Encoding]::UTF8)
    Write-Ok 'Form1.cs 补丁完成'
}

Write-Host ''
Write-Host '下一步：build.bat 或直接用 VS 打开 TrophyUnlocker.csproj 编译' -ForegroundColor Cyan
