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
    [Parameter(Mandatory = $true)][string]$RepoPath
)

$ErrorActionPreference = 'Stop'

function Write-Step($msg) { Write-Host "[*] $msg" -ForegroundColor Cyan }
function Write-Ok($msg)   { Write-Host "[+] $msg" -ForegroundColor Green }
function Write-Warn2($msg){ Write-Host "[!] $msg" -ForegroundColor Yellow }

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

if ($src -match 'TitleIdUtil\.ForGameNpTitle') {
    Write-Ok 'Form1.cs 已经是补丁后的状态，跳过'
    exit 0
}

# 3.1 newpkgid 先声明、后赋值（sfo 在它后面才创建，不能直接引用）
$before = $src
$src = $src -replace 'string\s+newpkgid\s*=\s*"XDPX30000_00"\s*;',
                     'string newpkgid;   // 补丁：下面按游戏 Title ID 计算'

if ($src -eq $before) {
    throw '没匹配到 string newpkgid = "XDPX30000_00"; —— 源码版本可能不一样，请手动确认'
}

# 3.2 sfo 创建之后立刻赋值
$before = $src
$src = $src -replace '(var\s+sfo\s*=\s*new\s+Param_SFO\.PARAM_SFO\([^;]*\)\s*;)',
                     "`$1`r`n            newpkgid = TitleIdUtil.ForGameNpTitle(sfo.TitleID);   // 补丁：唯一 Title ID"

if ($src -eq $before) {
    throw '没匹配到 var sfo = new Param_SFO.PARAM_SFO(...); —— 源码版本可能不一样，请手动确认'
}

# ---------- 4. 备份并写回 ----------
$bak = "$form.bak"
if (-not (Test-Path $bak)) {
    Copy-Item $form $bak
    Write-Ok "已备份 $bak"
}

[System.IO.File]::WriteAllText($form, $src, [System.Text.Encoding]::UTF8)
Write-Ok 'Form1.cs 补丁完成'

Write-Host ''
Write-Host '下一步：build.bat 或直接用 VS 打开 TrophyUnlocker.csproj 编译' -ForegroundColor Cyan
