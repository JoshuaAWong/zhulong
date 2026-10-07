$procs = Get-Process pythonw -ErrorAction SilentlyContinue
if (!$procs) { $procs = @() }
$mem = [math]::Round((($procs | Measure-Object WorkingSet64 -Sum).Sum / 1MB), 0)
$cpu = [math]::Round((($procs | Measure-Object CPU -Sum).Sum), 0)
$line = "{0} mem={1}MB cpu={2}s n={3}" -f (Get-Date -Format 'MM-dd HH:mm'), $mem, $cpu, $procs.Count
$logDir = Join-Path $PSScriptRoot "..\data\logs"
if (!(Test-Path $logDir)) { New-Item -ItemType Directory -Path $logDir -Force | Out-Null }
Add-Content (Join-Path $logDir "ac7.log") $line
