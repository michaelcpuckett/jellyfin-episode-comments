param([string]$Dotnet = 'dotnet')
$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'src/Jellyfin.Plugin.EpisodeComments/Jellyfin.Plugin.EpisodeComments.csproj'
& $Dotnet build $project -c Release --nologo
if ($LASTEXITCODE -ne 0) { throw 'Plugin build failed.' }
$package = Join-Path $PSScriptRoot 'dist/EpisodeComments_0.1.0.0'
New-Item -ItemType Directory -Force -Path $package | Out-Null
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'src/Jellyfin.Plugin.EpisodeComments/bin/Release/net10.0/Jellyfin.Plugin.EpisodeComments.dll') -Destination $package -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'meta.json') -Destination $package -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'LICENSE') -Destination $package -Force
$zip = Join-Path $PSScriptRoot 'dist/EpisodeComments-jellyfin12-0.1.0.zip'
Compress-Archive -LiteralPath $package -DestinationPath $zip -Force
Get-FileHash -LiteralPath $zip -Algorithm SHA256
