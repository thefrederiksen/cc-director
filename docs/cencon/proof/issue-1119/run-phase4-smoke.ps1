Set-Location -LiteralPath 'D:\ReposFred\devthrottle'
$out = 'D:\ReposFred\devthrottle\docs\cencon\proof\issue-1119'
$p = Start-Process -FilePath 'D:\ReposFred\devthrottle\tools\harnesses\terminal-inject-harness\bin\Debug\net10.0\terminal-inject-harness.exe' -ArgumentList @('--case','tiny','--route','direct','--runs','1','--timeout','90','--startup-timeout','90','--no-forced-parked','--allow-failures','--out',$out) -WorkingDirectory 'D:\ReposFred\devthrottle' -PassThru -Wait
Set-Content -LiteralPath 'D:\ReposFred\devthrottle\docs\cencon\proof\issue-1119\smoke-exit-code.txt' -Value $p.ExitCode -Encoding ascii
