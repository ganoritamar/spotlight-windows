# Measures how much CPU and memory Spotlight uses while idle.
#   .\measure.ps1             (60-second sample)
#   .\measure.ps1 -Seconds 300
param([int]$Seconds = 60)

$p = Get-Process Spotlight -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $p) { Write-Output "Spotlight is not running."; exit 1 }

$cores = [Environment]::ProcessorCount
$cpuStart = $p.TotalProcessorTime
$t0 = Get-Date
Start-Sleep -Seconds $Seconds
$p.Refresh()
$elapsed = ((Get-Date) - $t0).TotalMilliseconds
$cpuMs = ($p.TotalProcessorTime - $cpuStart).TotalMilliseconds
$pct = $cpuMs / $elapsed / $cores * 100

$privateWs = $null
try { $privateWs = (Get-Counter "\Process(Spotlight)\Working Set - Private" -ErrorAction Stop).CounterSamples[0].CookedValue } catch { }

"Idle sample:           {0} s" -f $Seconds
"CPU time used:         {0:n0} ms  ({1:n4} % of the whole CPU)" -f $cpuMs, $pct
"Memory (Task Manager): {0:n1} MB  (private working set)" -f ($privateWs / 1MB)
"Working set:           {0:n1} MB" -f ($p.WorkingSet64 / 1MB)
"Committed (private):   {0:n1} MB" -f ($p.PrivateMemorySize64 / 1MB)
"Threads / handles:     {0} / {1}" -f $p.Threads.Count, $p.HandleCount
