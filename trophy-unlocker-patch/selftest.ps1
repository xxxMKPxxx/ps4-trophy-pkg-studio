# 用 Form1.cs 的真实片段验证 apply-patch.ps1
$ErrorActionPreference = 'Stop'
$tmp = Join-Path $env:TEMP ("tup-test-" + [guid]::NewGuid().ToString('N').Substring(0,8))
New-Item -ItemType Directory -Path (Join-Path $tmp 'TrophyUnlocker') -Force | Out-Null

$form = @'
using System;
namespace TrophyUnlocker
{
    public partial class Form1 : Form
    {
        private void backgroundWorker2_DoWork(object sender, DoWorkEventArgs e)
        {
            File.WriteAllBytes(AppCommonPath() + @"CONTENTS\sce_sys\npbind.dat", result.npbind);
            string tmpcontentid = "EP2165-XDPX30000_00-TROPHYUNLOCKXXXX";
            //string tmpcontentid = "EP2165-CUSA09960_00-F13GAMEDISCXXXXX";
            string newpkgid = "XDPX30000_00";

            //now fix the nptitleid
            var sfo = new Param_SFO.PARAM_SFO(AppCommonPath() + @"CONTENTS\sce_sys\param.sfo");
            UpdateProgress("Fixing NpTitle");
            byte[] nptitle = File.ReadAllBytes(AppCommonPath() + @"CONTENTS\sce_sys\nptitle.dat");
            byte[] orginaltit =  Encoding.UTF8.GetBytes(sfo.TitleID+ "_00");
            byte[] newtit = Encoding.UTF8.GetBytes(newpkgid);
            nptitle =  ReplaceBytes(nptitle, orginaltit, newtit);
            File.WriteAllBytes(AppCommonPath() + @"CONTENTS\sce_sys\nptitle.dat", nptitle);
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

# 第一次打补丁（用子进程调用，和 build.bat 的方式一致）
& powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'apply-patch.ps1') -RepoPath $tmp
if ($LASTEXITCODE -ne 0) { throw "第一次执行失败, exit=$LASTEXITCODE" }

$patched = Get-Content (Join-Path $tmp 'TrophyUnlocker\Form1.cs') -Raw

Write-Host "`n=== 校验 ===" -ForegroundColor Cyan
$checks = @(
    @{ Name = '硬编码已移除';     Pass = ($patched -notmatch 'newpkgid\s*=\s*"XDPX30000_00"') },
    @{ Name = '声明保留';         Pass = ($patched -match 'string newpkgid;') },
    @{ Name = '赋值在 sfo 之后';  Pass = ($patched -match 'var sfo = new Param_SFO\.PARAM_SFO[^\r\n]*\r?\n\s*newpkgid = TitleIdUtil\.ForGameNpTitle\(sfo\.TitleID\);') },
    @{ Name = 'util 类已拷入';    Pass = (Test-Path (Join-Path $tmp 'TrophyUnlocker\TitleIdUtil.cs')) },
    @{ Name = 'csproj 已登记';    Pass = ((Get-Content (Join-Path $tmp 'TrophyUnlocker\TrophyUnlocker.csproj') -Raw) -match 'TitleIdUtil\.cs') },
    @{ Name = '.bak 已生成';      Pass = (Test-Path (Join-Path $tmp 'TrophyUnlocker\Form1.cs.bak')) }
)

$fail = 0
foreach ($c in $checks) {
    if ($c.Pass) { Write-Host ("  [OK]   " + $c.Name) -ForegroundColor Green }
    else         { Write-Host ("  [FAIL] " + $c.Name) -ForegroundColor Red; $fail++ }
}

# 幂等性：再跑一次
Write-Host "`n=== 幂等性 ===" -ForegroundColor Cyan
& powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'apply-patch.ps1') -RepoPath $tmp
$after = Get-Content (Join-Path $tmp 'TrophyUnlocker\Form1.cs') -Raw
if ($after -eq $patched) { Write-Host '  [OK]   重复执行不产生二次修改' -ForegroundColor Green }
else { Write-Host '  [FAIL] 重复执行把文件改坏了' -ForegroundColor Red; $fail++ }

Write-Host "`n=== 打补丁后的关键片段 ===" -ForegroundColor Cyan
($patched -split "`n") | Select-String -Pattern 'newpkgid|var sfo' | ForEach-Object { "  " + $_.Line.Trim() }

Remove-Item $tmp -Recurse -Force
if ($fail -gt 0) { Write-Host "`n失败 $fail 项" -ForegroundColor Red; exit 1 }
Write-Host "`n全部通过" -ForegroundColor Green
