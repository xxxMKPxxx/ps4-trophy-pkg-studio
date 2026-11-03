# 验证 FTP / 扫描重写相关补丁：端口、多目录扫描、逐条日志、文件名 Unicode
#
# 样例结构刻意贴近真实源码：
#   - newpkgid / sfo 在 backgroundWorker2_DoWork（生成 PKG 的那个方法）里
#   - backgroundWorker1_DoWork 才是 FTP 扫描
$ErrorActionPreference = 'Stop'
$tmp = Join-Path $env:TEMP ("tup-ftp-" + [guid]::NewGuid().ToString('N').Substring(0,8))
New-Item -ItemType Directory -Path (Join-Path $tmp 'TrophyUnlocker') -Force | Out-Null

$form = @'
using System;
namespace TrophyUnlocker
{
    public partial class Form1 : Form
    {
        private void backgroundWorker2_DoWork(object sender, DoWorkEventArgs e)
        {
            string newpkgid = "XDPX30000_00";
            var sfo = new Param_SFO.PARAM_SFO(AppCommonPath() + @"CONTENTS\sce_sys\param.sfo");
            string tmpcontentid = sfo.ContentID;
            foreach (var t in sfo.Tables)
            {
                if (t.Key == "TITLE_ID") { t.Value = newpkgid.Replace("_00", ""); }
            }
        }

        private void backgroundWorker1_DoWork(object sender, DoWorkEventArgs e)
        {
            try
            {
                gamelist = new List<GameInfo>();
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
                        try
                        {
                            GameInfo info = new GameInfo();
                            UpdateProgress("Game found " + splitt[8]);
                            byte[] paramsfo = DownloadFile(url + splitt[8] + "/param.sfo");
                            info.nptitle = DownloadFile(url + splitt[8] + "/nptitle.dat");
                            info.npbind = DownloadFile(url + splitt[8] + "/npbind.dat");
                            gamelist.Add(info);
                            var sfofile = PARAM_SFO.Get_Param_SFO(paramsfo);
                            info.ContentID = sfofile.ContentID;
                        }
                        catch (Exception ex)
                        {
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("Error " + ex.Message, "Error while loading game data");
            }
        }

        private void backgroundWorker1_RunWorkerCompleted(object sender, RunWorkerCompletedEventArgs e)
        {
            for (int i = 0; i < gamelist.Count; i++)
            {
                comboBox1.Items.Add(gamelist[i].Title);
            }
            if (gamelist.Count != 0)
            {
                comboBox1.SelectedIndex = 0;
            }

            pnlProgress.Visible = false;
        }

        private void comboBox1_SelectedIndexChanged(object sender, EventArgs e)
        {
            var result = gamelist.Single(s => s.Title == comboBox1.SelectedItem);
            label8.Text = result.Title;
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
  @{ N='端口已改为 2121';            Pass = ($p -match ':2121/') },
  @{ N='扫描 user/appmeta/';         Pass = ($p -match '"user/appmeta/"') },
  @{ N='扫描 appmeta/';              Pass = ($p -match '"appmeta/"') },
  @{ N='扫描 ext0 外置盘';           Pass = ($p -match '"mnt/ext0/user/appmeta/"') },
  @{ N='DoWork 已重写';              Pass = ($p -match '补丁：扫描两个 appmeta 目录') },
  @{ N='列表先清空';                 Pass = ($p -match '补丁：每次扫描先清空') },
  @{ N='Items.Clear 只一次';         Pass = (([regex]::Matches($p, 'comboBox1\.Items\.Clear\(\)')).Count -eq 1) },
  @{ N='Items.Add 只一次';           Pass = (([regex]::Matches($p, 'comboBox1\.Items\.Add')).Count -eq 1) },
  @{ N='Single 已换 FirstOrDefault'; Pass = ($p -notmatch 'gamelist\.Single\(' -and $p -match 'gamelist\.FirstOrDefault\(') },
  @{ N='已无 splitt 下标';           Pass = ($p -notmatch 'splitt\[\d\]') },
  @{ N='LooksLikeDir 已定义';        Pass = (([regex]::Matches($p, 'private static bool LooksLikeDir')).Count -eq 1) },
  @{ N='LastField 已定义';           Pass = (([regex]::Matches($p, 'private static string LastField')).Count -eq 1) },
  @{ N='按 TitleId 去重';            Pass = ($p -match 'seen\.Contains\(info\.TitleId\)') },
  @{ N='无空 catch';                 Pass = ($p -notmatch '(?s)catch \(Exception ex\)\s*\{\s*\}') },
  @{ N='DownloadFile 已容错';        Pass = ($p -match '补丁：失败不抛异常' -and $p -match 'return new byte\[0\];') },
  @{ N='DownloadFile 已设超时';      Pass = ($p -match 'request\.Timeout = 20000') },
  @{ N='DownloadFile 只剩一份';      Pass = (([regex]::Matches($p, 'private byte\[\] DownloadFile')).Count -eq 1) },
  @{ N='DownloadFile 有重试循环';    Pass = ($p -match 'for \(int attempt = 0; attempt < 3; attempt\+\+\)') },
  @{ N='跳过原因已可见';             Pass = ($p -match '补丁：记录跳过原因') },
  @{ N='已注入 LogLine 方法';        Pass = ($p -match '补丁：扫描日志' -and $p -match 'trophylog\.txt') },
  @{ N='LogLine 调用 >= 8 处';       Pass = (([regex]::Matches($p, 'LogLine\(')).Count -ge 8) },
  @{ N='日志含扫描起点';             Pass = ($p -match '=== 开始扫描 appmeta ===') },
  @{ N='日志含成功记录';             Pass = ($p -match 'LogLine\("OK   "') },
  @{ N='TitleId 唯一化仍在';         Pass = (([regex]::Matches($p, 'TitleIdUtil\.ForGameNpTitle')).Count -eq 1) },
  @{ N='newpkgid 硬编码已移除';      Pass = ($p -notmatch '"XDPX30000_00"') },
  @{ N='大括号仍平衡';               Pass = ((([regex]::Matches($p,'\{')).Count) -eq (([regex]::Matches($p,'\}')).Count)) }
)
$fail = 0
foreach ($c in $checks) {
  if ($c.Pass) { Write-Host ("  [OK]   " + $c.N) -ForegroundColor Green }
  else { Write-Host ("  [FAIL] " + $c.N) -ForegroundColor Red; $fail++ }
}

Write-Host "`n=== 文件名净化：中日韩标题 ===" -ForegroundColor Cyan
$names = @('The Last of Us Part II', '龍が如く 極', '最后生还者 第二章', 'Game: Part 2 / Remaster?', '///', 'CON')
foreach ($n in $names) {
  $sb = New-Object System.Text.StringBuilder
  foreach ($ch in $n.ToCharArray()) {
    if ([char]::IsLetterOrDigit($ch) -or $ch -eq ' ' -or $ch -eq '-' -or $ch -eq '_' -or $ch -eq '.') { [void]$sb.Append($ch) }
    elseif ([char]::IsWhiteSpace($ch)) { [void]$sb.Append(' ') }
  }
  $out = $sb.ToString()
  if ($out -eq '') { $out = 'unlocker' }
  $reserved = @('CON','PRN','AUX','NUL','COM1','COM9','LPT1','LPT9')
  if ($reserved -contains $out.ToUpperInvariant()) { $out = '_' + $out }
  "  '{0}' -> '{1}'" -f $n, $out
}

Write-Host "`n=== 幂等性 ===" -ForegroundColor Cyan
& powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'apply-patch.ps1') -RepoPath $tmp | Out-Null
$p2 = Get-Content (Join-Path $tmp 'TrophyUnlocker\Form1.cs') -Raw
if ($p2 -eq $p) { Write-Host '  [OK]   重复执行不改动' -ForegroundColor Green }
else { Write-Host '  [FAIL] 重复执行改坏了' -ForegroundColor Red; $fail++ }

Remove-Item $tmp -Recurse -Force
if ($fail -gt 0) { Write-Host "`n失败 $fail 项" -ForegroundColor Red; exit 1 }
Write-Host "`n全部通过" -ForegroundColor Green
