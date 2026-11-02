# Builds an optimized copy of Spotlight into %LOCALAPPDATA%\Programs\Spotlight and starts it.
# Run again any time to update. Your settings and usage history are kept.
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$dest = Join-Path $env:LOCALAPPDATA 'Programs\Spotlight'
$exe = Join-Path $dest 'Spotlight.exe'

$dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
if (-not $dotnet) { $dotnet = Join-Path $env:ProgramFiles 'dotnet\dotnet.exe' }

# Ask a running copy to quit cleanly (saves its index, removes its tray icon).
if (Test-Path $exe) { & $exe --quit; Start-Sleep -Seconds 1 }
Get-Process Spotlight -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue

& $dotnet publish (Join-Path $root 'src\Spotlight\Spotlight.csproj') -c Release -r win-x64 --self-contained false `
    -p:PublishReadyToRun=true -p:DebugType=none -o $dest
if ($LASTEXITCODE -ne 0) { throw "Build failed" }

Start-Process $exe
Write-Output "Installed to $dest and started. Press Ctrl+Space."
