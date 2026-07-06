Set-Location -LiteralPath 'D:\ReposFred\devthrottle'
$p = Start-Process -FilePath 'D:\ReposFred\devthrottle\tools\harnesses\terminal-inject-harness\bin\Debug\net10.0\terminal-inject-harness.exe' -ArgumentList @('--runs','1','--timeout','240','--startup-timeout','150','--out','D:\ReposFred\devthrottle\docs\cencon\proof\issue-1117') -WorkingDirectory 'D:\ReposFred\devthrottle' -PassThru -Wait
Set-Content -LiteralPath 'D:\ReposFred\devthrottle\docs\cencon\proof\issue-1117\exit-code.txt' -Value $p.ExitCode -Encoding ascii
exit $p.ExitCode
