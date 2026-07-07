Set-Location -LiteralPath 'D:\ReposFred\devthrottle'
$out = 'D:\ReposFred\devthrottle\docs\cencon\proof\issue-1119'
$p = Start-Process -FilePath 'D:\ReposFred\devthrottle\tools\harnesses\terminal-inject-harness\bin\Debug\net10.0\terminal-inject-harness.exe' -ArgumentList @('--focused-phase4','--submit-strategy','current','--timeout','180','--startup-timeout','150','--no-forced-parked','--allow-failures','--out',$out) -WorkingDirectory 'D:\ReposFred\devthrottle' -PassThru -Wait
Set-Content -LiteralPath 'D:\ReposFred\devthrottle\docs\cencon\proof\issue-1119\exit-code.txt' -Value $p.ExitCode -Encoding ascii
