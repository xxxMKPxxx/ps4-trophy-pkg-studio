# 验证 FTP 相关补丁：端口 + 目录列表解析
$ErrorActionPreference = 'Stop'
$tmp = Join-Path $env:TEMP ("tup-ftp-" + [guid]::NewGuid().ToString('N').Substring(0,8))
New-Item -ItemType Directory -Path (Join-Path $tmp 'TrophyUnlocker') -Force | Out-Null

$form = @'
using System;
namespace TrophyUnlocker
{
    public partial class Form1 : Form
    {
        private void backgroundWorker1_DoWork(object sender, DoWorkEventArgs e)
        {
            string newpkgid = "XDPX30000_00";
            var sfo = new Param_SFO.PARAM_SFO(AppCommonPath() + @"CONTENTS\sce_sys\param.sfo");

            string url = "ftp://" + textBox1.Text + ":1337/user/home/";
            url = "ftp://" + textBox1.Text + ":1337/system_data/priv/appmeta/";
            string tmpurl = "ftp://" + textBox1.Text + ":1337/user/trophy/conf/" + bindinfo.Nptitle + "/";

            var splitt = line.Split(' ');
            if (splitt.Length == 10)//only do this when 10 items in array
            {
                byte[] userinfo = DownloadFile(url + splitt[9] + "/username.dat");
                info.UserId = splitt[9];
            }

            if (splitt.Length == 9)//only do this when 10 items in array
            {
                if (splitt[8] != "..")
                {
                    UpdateProgress("Game found " + splitt[8]);
                    byte[] paramsfo = DownloadFile(url + splitt[8] + "/param.sfo");
                    info.nptitle = DownloadFile(url + splitt[8] + "/nptitle.dat");
                    info.npbind = DownloadFile(url + splitt[8] + "/npbind.dat");
                }
            }

            try
            {
                byte[] paramsfo = DownloadFile(url + "CUSA00001/param.sfo");
                var sfofile = PARAM_SFO.Get_Param_SFO(paramsfo);
                info.ContentID = sfofile.ContentID;
            }
            catch (Exception ex)
            {
            }
        }

        public static string AppCommonPath()
        {
            return "x";
        }

        public static string RemoveSpecialCharacters(string str)
        {
            StringBuilder sb = new StringBuilder();
            foreach (char c in str)
            {
                if ((c >= '0' && c <= '9') || c == '.' || c == '_' || c == ' ')
                {
                    sb.Append(c);
                }
            }
            return sb.ToString();
        }

        private byte[] DownloadFile(string ftpSourceFilePath)
        {
            FtpWebRequest request = (FtpWebRequest)WebRequest.Create(ftpSourceFilePath);
            request.Credentials = new NetworkCredential("anonymous", "janeDoe@contoso.com");
            request.Method = WebRequestMethods.Ftp.DownloadFile;
            request.UsePassive = true;
            request.Method = WebRequestMethods.Ftp.DownloadFile;

            Stream reader = request.GetResponse().GetResponseStream();
            byte[] buffer = ReadFully(reader);
            return buffer;
        }
    }
}
'@

Set-Content (Join-Path $tmp 'TrophyUnlocker\Form1.cs') -Value $form -Encoding UTF8
Write-Host "临时仓库: $tmp" -ForegroundColor DarkGray

& powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'apply-patch.ps1') -RepoPath $tmp
if ($LASTEXITCODE -ne 0) { throw "执行失败 exit=$LASTEXITCODE" }

$p = Get-Content (Join-Path $tmp 'TrophyUnlocker\Form1.cs') -Raw

Write-Host "`n=== 结构校验 ===" -ForegroundColor Cyan
$checks = @(
  @{ N='已无 :1337/';                Pass = ($p -notmatch ':1337/') },
  @{ N=':2121/ 出现 3 次';           Pass = (([regex]::Matches($p, ':2121/')).Count -eq 3) },
  @{ N='已无 splitt[8]/splitt[9]';   Pass = ($p -notmatch 'splitt\[8\]|splitt\[9\]') },
  @{ N='末位取值出现 12 次';         Pass = (([regex]::Matches($p, 'splitt\[splitt\.Length - 1\]')).Count -eq 12) },
  @{ N='字段数判断放宽为 >= 9';      Pass = (([regex]::Matches($p, 'splitt\.Length >= 9')).Count -eq 2) },
  @{ N='已排除 . 和 ..';             Pass = (([regex]::Matches($p, '!= "\.\."')).Count -eq 3) },
  @{ N='DownloadFile 已容错';        Pass = ($p -match '补丁：失败不抛异常' -and $p -match 'return new byte\[0\];') },
  @{ N='DownloadFile 已设超时';      Pass = ($p -match 'request\.Timeout = 20000') },
  @{ N='DownloadFile 只剩一份';      Pass = (([regex]::Matches($p, 'private byte\[\] DownloadFile')).Count -eq 1) },
  @{ N='DownloadFile 有重试循环';    Pass = ($p -match 'for \(int attempt = 0; attempt < 3; attempt\+\+\)' -and $p -match 'Thread\.Sleep\(300\)') },
  @{ N='跳过原因已可见';             Pass = ($p -match '补丁：记录跳过原因' -and $p -notmatch '(?s)catch \(Exception ex\)\s*\{\s*\}') },
  @{ N='已注入 LogLine 方法';        Pass = ($p -match '补丁：扫描日志' -and $p -match 'trophylog\.txt') },
  @{ N='LogLine 调用 >= 3 处';       Pass = (([regex]::Matches($p, 'LogLine\(')).Count -ge 3) },
  @{ N='日志含扫描起点';             Pass = ($p -match '=== 开始扫描 appmeta ===') },
  @{ N='日志含成功记录';             Pass = ($p -match 'LogLine\("OK   "') },
  @{ N='大括号仍平衡';               Pass = ((([regex]::Matches($p,'\{')).Count) -eq (([regex]::Matches($p,'\}')).Count)) }
)
$fail = 0
foreach ($c in $checks) {
  if ($c.Pass) { Write-Host ("  [OK]   " + $c.N) -ForegroundColor Green }
  else { Write-Host ("  [FAIL] " + $c.N) -ForegroundColor Red; $fail++ }
}

Write-Host "`n=== ls 行解析：旧逻辑 vs 新逻辑 ===" -ForegroundColor Cyan
$lines = @(
  'drwxr-xr-x 2 root root 4096 Jan  1 00:00 10000000',
  'drwxr-xr-x 2 root root 4096 Nov 14 21:30 10000001',
  'drwxr-xr-x 2 root root 4096 Jan  1 00:00 ..',
  'total 8'
)
foreach ($l in $lines) {
  $sp = $l.Split([char]' ')
  # 旧：/user/home/ 要求恰好 10 个字段，取 index 9
  $old = if ($sp.Length -eq 10) { $sp[9] } else { '<跳过>' }
  # 新：>= 9 个字段，取最后一个
  $new = if ($sp.Length -ge 9) { $sp[$sp.Length - 1] } else { '<跳过>' }
  $mark = if ($old -eq $new) { '  ' } else { ' *' }
  "{0} fields={1,-2} 旧={2,-10} 新={3,-10} [{4}]" -f $mark, $sp.Length, $old, $new, ($l -replace '\s+',' ')
}

Write-Host "`n=== 幂等性 ===" -ForegroundColor Cyan
& powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'apply-patch.ps1') -RepoPath $tmp | Out-Null
$p2 = Get-Content (Join-Path $tmp 'TrophyUnlocker\Form1.cs') -Raw
if ($p2 -eq $p) { Write-Host '  [OK]   重复执行不改动' -ForegroundColor Green }
else { Write-Host '  [FAIL] 重复执行改坏了' -ForegroundColor Red; $fail++ }

Remove-Item $tmp -Recurse -Force
if ($fail -gt 0) { Write-Host "`n失败 $fail 项" -ForegroundColor Red; exit 1 }
Write-Host "`n全部通过" -ForegroundColor Green
