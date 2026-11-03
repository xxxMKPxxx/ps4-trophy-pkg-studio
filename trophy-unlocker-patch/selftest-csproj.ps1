# 验证 -ReferenceDir 能修好两处断掉的引用（用 PS4_Tools 真实 csproj 结构）
$ErrorActionPreference = 'Stop'

$tmp = Join-Path $env:TEMP ("tup-csproj-" + [guid]::NewGuid().ToString('N').Substring(0,8))
$projDir = Join-Path $tmp 'TrophyUnlocker'
$refDir  = Join-Path $projDir 'bin\Release'
New-Item -ItemType Directory -Path $projDir -Force | Out-Null
New-Item -ItemType Directory -Path $refDir  -Force | Out-Null

# 假的预编译 DLL，只为让脚本的 Test-Path 通过
'x' | Set-Content (Join-Path $refDir 'PS4_Tools.dll')
'x' | Set-Content (Join-Path $refDir 'PARAM.SFO.dll')

$csproj = @'
<?xml version="1.0" encoding="utf-8"?>
<Project ToolsVersion="14.0" DefaultTargets="Build" xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
  <PropertyGroup>
    <OutputType>WinExe</OutputType>
    <TargetFrameworkVersion>v4.5.2</TargetFrameworkVersion>
  </PropertyGroup>
  <ItemGroup>
    <Reference Include="PARAM.SFO">
      <HintPath>..\..\..\..\GitHub\PS3Tools\PARAM.SFO Editor\PARAM.SFO\bin\Debug\PARAM.SFO.dll</HintPath>
    </Reference>
    <Reference Include="System" />
    <Reference Include="System.Windows.Forms" />
  </ItemGroup>
  <ItemGroup>
    <Compile Include="Form1.cs">
      <SubType>Form</SubType>
    </Compile>
    <Compile Include="Program.cs" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\PS4_Tools\PS4_Tools.csproj">
      <Project>{095271bf-9f85-43d0-bf8e-782b18eaed6f}</Project>
      <Name>PS4_Tools</Name>
    </ProjectReference>
  </ItemGroup>
  <Import Project="$(MSBuildToolsPath)\Microsoft.CSharp.targets" />
</Project>
'@

$form = @'
namespace TrophyUnlocker
{
    public partial class Form1
    {
        private void backgroundWorker2_DoWork(object sender, System.ComponentModel.DoWorkEventArgs e)
        {
            string newpkgid = "XDPX30000_00";

            var sfo = new Param_SFO.PARAM_SFO(AppCommonPath() + @"CONTENTS\sce_sys\param.sfo");
            byte[] newtit = Encoding.UTF8.GetBytes(newpkgid);
        }
    }
}
'@

Set-Content (Join-Path $projDir 'TrophyUnlocker.csproj') -Value $csproj -Encoding UTF8
Set-Content (Join-Path $projDir 'Form1.cs') -Value $form -Encoding UTF8

Write-Host "临时仓库: $tmp" -ForegroundColor DarkGray
& powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'apply-patch.ps1') `
    -RepoPath $tmp -ReferenceDir $refDir
if ($LASTEXITCODE -ne 0) { throw "执行失败 exit=$LASTEXITCODE" }

$out = Get-Content (Join-Path $projDir 'TrophyUnlocker.csproj') -Raw
$f   = Get-Content (Join-Path $projDir 'Form1.cs') -Raw

Write-Host "`n=== 校验 ===" -ForegroundColor Cyan
$checks = @(
  @{ N='PARAM.SFO HintPath 已改'; Pass = ($out -match 'PARAM\.SFO\.dll' -and $out -notmatch 'GitHub\\PS3Tools') },
  @{ N='ProjectReference 已移除'; Pass = ($out -notmatch 'ProjectReference') },
  @{ N='PS4_Tools 引用已加入';    Pass = ($out -match '<Reference Include="PS4_Tools">') },
  @{ N='仍有 msbuild 命名空间';   Pass = ($out -match 'schemas\.microsoft\.com/developer/msbuild/2003') },
  @{ N='其它 Reference 未丢失';   Pass = ($out -match 'System\.Windows\.Forms') },
  @{ N='Form1 补丁生效';          Pass = ($f -match 'TitleIdUtil\.ForGameNpTitle\(sfo\.TitleID\)') },
  @{ N='幂等：再跑一次';          Pass = $true }
)

$fail = 0
foreach ($c in $checks) { if ($c.Pass) { Write-Host ("  [OK]   " + $c.N) -ForegroundColor Green } else { Write-Host ("  [FAIL] " + $c.N) -ForegroundColor Red; $fail++ } }

& powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'apply-patch.ps1') `
    -RepoPath $tmp -ReferenceDir $refDir | Out-Null
if ($LASTEXITCODE -ne 0) { Write-Host '  [FAIL] 第二次执行出错' -ForegroundColor Red; $fail++ }
else { Write-Host '  [OK]   第二次执行正常（幂等）' -ForegroundColor Green }

Write-Host "`n=== 改写后的引用 ===" -ForegroundColor Cyan
($out -split "`n") | Select-String -Pattern 'Reference|HintPath' | ForEach-Object { "  " + $_.Line.Trim() }

Remove-Item $tmp -Recurse -Force
if ($fail -gt 0) { Write-Host "`n失败 $fail 项" -ForegroundColor Red; exit 1 }
Write-Host "`n全部通过" -ForegroundColor Green
