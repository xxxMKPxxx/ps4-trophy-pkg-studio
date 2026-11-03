# 验证 apply-patch.ps1：Title ID + 文件名净化
$ErrorActionPreference = 'Stop'
$tmp = Join-Path $env:TEMP ("tup-test-" + [guid]::NewGuid().ToString('N').Substring(0,8))
New-Item -ItemType Directory -Path (Join-Path $tmp 'TrophyUnlocker') -Force | Out-Null

# 用 Form1.cs 的真实结构：包含原版 RemoveSpecialCharacters 和它前后的代码
$form = @'
using System;
using System.Text;
namespace TrophyUnlocker
{
    public partial class Form1 : Form
    {
        private void backgroundWorker2_DoWork(object sender, DoWorkEventArgs e)
        {
            string tmpcontentid = "EP2165-XDPX30000_00-TROPHYUNLOCKXXXX";
            string newpkgid = "XDPX30000_00";

            var sfo = new Param_SFO.PARAM_SFO(AppCommonPath() + @"CONTENTS\sce_sys\param.sfo");
            byte[] newtit = Encoding.UTF8.GetBytes(newpkgid);

            UpdateProgress("Renaming PKG");
            if (File.Exists(saveFileDialog1.SelectedPath + @"\" + RemoveSpecialCharacters(sfo.Title) + ".pkg"))
            {
                File.Delete(saveFileDialog1.SelectedPath + @"\" + RemoveSpecialCharacters(sfo.Title) + ".pkg");
            }
            File.Move(saveFileDialog1.SelectedPath + @"\" + sfo.ContentID + "-A" + sfo.APP_VER.Replace(".", "") + "-V" + version.Replace(".", "") + ".pkg",
                      saveFileDialog1.SelectedPath + @"\" + RemoveSpecialCharacters(sfo.Title) + ".pkg");
        }

        public static string RemoveSpecialCharacters(string str)
        {
            StringBuilder sb = new StringBuilder();
            foreach (char c in str)
            {
                if ((c >= '0' && c <= '9') || (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || c == '.' || c == '_' || c == ' ')
                {
                    sb.Append(c);
                }
            }
            return sb.ToString();
        }
    }
}
'@

$csproj = @'
<Project ToolsVersion="15.0" xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
  <ItemGroup>
    <Compile Include="Form1.cs">
      <SubType>Form</SubType>
    </Compile>
  </ItemGroup>
</Project>
'@

Set-Content -Path (Join-Path $tmp 'TrophyUnlocker\Form1.cs') -Value $form -Encoding UTF8
Set-Content -Path (Join-Path $tmp 'TrophyUnlocker\TrophyUnlocker.csproj') -Value $csproj -Encoding UTF8

Write-Host "临时仓库: $tmp" -ForegroundColor DarkGray

& powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'apply-patch.ps1') -RepoPath $tmp
if ($LASTEXITCODE -ne 0) { throw "第一次执行失败, exit=$LASTEXITCODE" }

$patched = Get-Content (Join-Path $tmp 'TrophyUnlocker\Form1.cs') -Raw

Write-Host "`n=== 结构校验 ===" -ForegroundColor Cyan
$open  = ([regex]::Matches($patched, '\{')).Count
$close = ([regex]::Matches($patched, '\}')).Count
$defs  = ([regex]::Matches($patched, 'public static string RemoveSpecialCharacters')).Count

$checks = @(
    @{ Name = '硬编码 Title ID 已移除'; Pass = ($patched -notmatch 'newpkgid\s*=\s*"XDPX30000_00"') },
    @{ Name = '声明保留';               Pass = ($patched -match 'string newpkgid;') },
    @{ Name = '赋值在 sfo 之后';        Pass = ($patched -match 'var sfo = new Param_SFO\.PARAM_SFO[^\r\n]*\r?\n\s*newpkgid = TitleIdUtil\.ForGameNpTitle\(sfo\.TitleID\);') },
    @{ Name = 'util 类已拷入';          Pass = (Test-Path (Join-Path $tmp 'TrophyUnlocker\TitleIdUtil.cs')) },
    @{ Name = 'csproj 已登记';          Pass = ((Get-Content (Join-Path $tmp 'TrophyUnlocker\TrophyUnlocker.csproj') -Raw) -match 'TitleIdUtil\.cs') },
    @{ Name = '.bak 已生成';            Pass = (Test-Path (Join-Path $tmp 'TrophyUnlocker\Form1.cs.bak')) },
    @{ Name = '旧文件名逻辑已消失';     Pass = ($patched -notmatch "c >= '0' && c <= '9'") },
    @{ Name = '新文件名逻辑已生效';     Pass = ($patched -match 'char\.IsLetterOrDigit') },
    @{ Name = '方法只有一份定义';       Pass = ($defs -eq 1) },
    @{ Name = '大括号仍平衡';           Pass = ($open -eq $close) },
    @{ Name = '方法后的调用代码还在';   Pass = ($patched -match 'Renaming PKG' -and $patched -match 'File\.Move') }
)

$fail = 0
foreach ($c in $checks) {
    if ($c.Pass) { Write-Host ("  [OK]   " + $c.Name) -ForegroundColor Green }
    else         { Write-Host ("  [FAIL] " + $c.Name) -ForegroundColor Red; $fail++ }
}

Write-Host "`n=== 净化逻辑等价验证（PowerShell 复刻同一算法）===" -ForegroundColor Cyan
function Sanitize([string]$str) {
    if ([string]::IsNullOrEmpty($str)) { return 'unlocker' }
    $sb = New-Object System.Text.StringBuilder
    foreach ($ch in $str.ToCharArray()) {
        if ([char]::IsLetterOrDigit($ch) -or $ch -eq ' ' -or $ch -eq '-' -or $ch -eq '_' -or $ch -eq '.') { [void]$sb.Append($ch) }
        elseif ([char]::IsWhiteSpace($ch)) { [void]$sb.Append(' ') }
    }
    $s = $sb.ToString().Trim().TrimEnd('.')
    if ($s.Length -eq 0) { $s = 'unlocker' }
    if ($s.Length -gt 100) { $s = $s.Substring(0, 100).TrimEnd('.', ' ') }
    return $s
}
$cases = @(
    @{ In = '龍が如く 極';             Want = '龍が如く 極' },
    @{ In = '最后生还者 第二章';       Want = '最后生还者 第二章' },
    @{ In = 'The Last of Us Part II';  Want = 'The Last of Us Part II' },
    @{ In = 'Game: Part 2 / Remaster?'; Want = 'Game Part 2  Remaster' },
    @{ In = '///';                     Want = 'unlocker' },
    @{ In = '';                        Want = 'unlocker' }
)
foreach ($c in $cases) {
    $got = Sanitize $c.In
    if ($got -eq $c.Want) { Write-Host ("  [OK]   '{0}' -> '{1}'" -f $c.In, $got) -ForegroundColor Green }
    else { Write-Host ("  [FAIL] '{0}' -> '{1}'  (期望 '{2}')" -f $c.In, $got, $c.Want) -ForegroundColor Red; $fail++ }
}

Write-Host "`n=== 幂等性 ===" -ForegroundColor Cyan
& powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'apply-patch.ps1') -RepoPath $tmp
$after = Get-Content (Join-Path $tmp 'TrophyUnlocker\Form1.cs') -Raw
if ($after -eq $patched) { Write-Host '  [OK]   重复执行不产生二次修改' -ForegroundColor Green }
else { Write-Host '  [FAIL] 重复执行把文件改坏了' -ForegroundColor Red; $fail++ }

Write-Host "`n=== 补丁后的方法（前 12 行）===" -ForegroundColor Cyan
$m = [regex]::Match($patched, 'public static string RemoveSpecialCharacters[\s\S]*?\n        \}')
($m.Value -split "`n") | Select-Object -First 12 | ForEach-Object { "  " + $_.TrimEnd() }

Remove-Item $tmp -Recurse -Force
if ($fail -gt 0) { Write-Host "`n失败 $fail 项" -ForegroundColor Red; exit 1 }
Write-Host "`n全部通过" -ForegroundColor Green
