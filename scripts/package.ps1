# Builds the download zips for a GitHub release into .\dist
#   Spotlight-<version>-win-x64.zip               small; needs the free .NET 8 Desktop Runtime
#   Spotlight-<version>-win-x64-standalone.zip    bigger; runs on any Windows 10/11 PC as is
# (and the same two for win-arm64, for Windows on Snapdragon/ARM laptops)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$project = Join-Path $root 'src\Spotlight\Spotlight.csproj'
$dist = Join-Path $root 'dist'

$dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
if (-not $dotnet) { $dotnet = Join-Path $env:ProgramFiles 'dotnet\dotnet.exe' }

$version = ([xml](Get-Content $project)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if (Test-Path $dist) { Remove-Item $dist -Recurse -Force }
New-Item -ItemType Directory $dist | Out-Null

foreach ($rid in 'win-x64', 'win-arm64') {
    foreach ($standalone in $false, $true) {
        $name = "Spotlight-$version-$rid" + $(if ($standalone) { '-standalone' } else { '' })
        $out = Join-Path $dist "stage\$name\Spotlight"
        Write-Output "Building $name ..."
        & $dotnet publish $project -c Release -r $rid --self-contained $standalone.ToString().ToLower() `
            -p:PublishReadyToRun=true -p:DebugType=none -o $out -nologo -v q
        if ($LASTEXITCODE -ne 0) { throw "Build failed: $name" }
        Compress-Archive -Path $out -DestinationPath (Join-Path $dist "$name.zip") -CompressionLevel Optimal
    }
}

Remove-Item (Join-Path $dist 'stage') -Recurse -Force
Get-ChildItem $dist -Filter *.zip | ForEach-Object { '{0,-48} {1,6:N1} MB' -f $_.Name, ($_.Length / 1MB) }
