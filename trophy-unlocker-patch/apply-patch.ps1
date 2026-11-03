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
Write-Step '安装 TitleIdUtil.cs'
Copy-Item $utilSrc $utilDst -Force
Write-Ok "已写入 $utilDst"

# ---------- 2. 登记到 csproj ----------
if (Test-Path $csproj) {
    $xml = Get-Content $csproj -Raw -Encoding UTF8
    if ($xml -notmatch 'TitleIdUtil\.cs') {
        # 挂在 Form1.cs 后面，老式 csproj 用显式 Compile 项
        # 直接在 </Project> 前加一个独立 ItemGroup。
        # 不去正则匹配已有的 <Compile Include="Form1.cs">，因为真项目里它带 <SubType> 子节点，
        # 是 <Compile ...>...</Compile> 而不是自闭合写法，按行匹配必失败。
        if ($xml -match '</Project>') {
            $inject = "  <ItemGroup>`r`n    <Compile Include=`"TitleIdUtil.cs`" />`r`n  </ItemGroup>`r`n</Project>"
            $xml = $xml -replace '</Project>', $inject
            [System.IO.File]::WriteAllText($csproj, $xml, [System.Text.Encoding]::UTF8)
            Write-Ok '已把 TitleIdUtil.cs 登记到 csproj'
        } else {
            Write-Warn2 'csproj 里找不到 </Project>，请手动把 TitleIdUtil.cs 加进项目'
        }
    } else {
        Write-Ok 'csproj 里已有 TitleIdUtil.cs，跳过'
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
    $src = Replace-CSharpMethod -Source $src `
        -Signature 'public static string RemoveSpecialCharacters(string str)' `
        -NewText $newMethod
    Write-Ok 'RemoveSpecialCharacters 已改为保留 Unicode'
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
    $src = Replace-CSharpMethod -Source $src `
        -Signature 'private byte[] DownloadFile(string ftpSourceFilePath)' `
        -NewText $newDownload
    Write-Ok 'DownloadFile 已改为容错 + 重试 3 次 + 20 秒超时'
} else {
    Write-Ok 'DownloadFile 补丁已存在，跳过'
}

# --- 3.6 让"跳过"可见 ---
# appmeta 扫描里每个游戏被空 catch 包着，失败就静默消失，
# 这正好是"能取到但不全"最难查的地方。把原因打到进度条上。
$needSkipLogPatch = ($src -notmatch '补丁：记录跳过原因')
if ($needSkipLogPatch) {
    $before = $src
    $src = $src -replace '(?s)catch \(Exception ex\)\s*\{\s*\}',
        "catch (Exception ex)`r`n                {`r`n                    UpdateProgress(`"补丁：记录跳过原因 `" + ex.Message);`r`n                    LogLine(`"跳过 `" + splitt[splitt.Length - 1] + `" : `" + ex.Message);`r`n                }"
    if ($src -eq $before) {
        Write-Warn2 '没找到空的 catch (Exception ex) { }，跳过该补丁'
    } else {
        Write-Ok '被跳过的游戏会记录到日志'
    }
} else {
    Write-Ok '跳过原因提示已存在，跳过'
}

# --- 3.7 扫描日志文件 ---
# 进度条刷得太快看不清，落一份文件到 exe 同目录。
$needLogPatch = ($src -notmatch '补丁：扫描日志')
if ($needLogPatch) {
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
    $src = Insert-AfterCSharpMethod -Source $src -Signature 'public static string AppCommonPath()' -Text $logMethod

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
    Write-Ok '扫描日志已存在，跳过'
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
if ($needTitlePatch -or $needNamePatch -or $needFtpPortPatch -or $needFtpListPatch -or $needDownloadPatch -or $needSkipLogPatch -or $needLogPatch) {
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
