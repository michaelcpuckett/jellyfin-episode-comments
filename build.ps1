param(
    [string]$Dotnet = 'dotnet',
    [switch]$SkipBuild,
    [ValidatePattern('^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$')]
    [string]$Repository = 'michaelcpuckett/jellyfin-episode-comments',
    [string]$Branch = 'master'
)
$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'src/Jellyfin.Plugin.EpisodeComments/Jellyfin.Plugin.EpisodeComments.csproj'
$dll = Join-Path $PSScriptRoot 'Jellyfin.Plugin.EpisodeComments.dll'
if (!$SkipBuild) {
    & $Dotnet build $project -c Release --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Plugin build failed.' }
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'src/Jellyfin.Plugin.EpisodeComments/bin/Release/net10.0/Jellyfin.Plugin.EpisodeComments.dll') -Destination $dll -Force
}
if (!(Test-Path -LiteralPath $dll)) { throw 'Build the plugin first, or provide the existing DLL before using -SkipBuild.' }
$metadata = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'meta.json') -Raw | ConvertFrom-Json
$assemblyVersion = [System.Reflection.AssemblyName]::GetAssemblyName($dll).Version.ToString()
if ($assemblyVersion -ne $metadata.version) { throw 'DLL version and meta.json version disagree.' }
# Keep the folder-wrapped archive for manual extraction into the plugins directory.
$shortVersion = [version]::Parse($metadata.version).ToString(3)
$package = Join-Path $PSScriptRoot "dist/EpisodeComments_$($metadata.version)"
New-Item -ItemType Directory -Force -Path $package | Out-Null
Copy-Item -LiteralPath $dll -Destination $package -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'meta.json') -Destination $package -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'LICENSE') -Destination $package -Force
$zip = Join-Path $PSScriptRoot "dist/EpisodeComments-jellyfin12-$shortVersion.zip"
Compress-Archive -LiteralPath $package -DestinationPath $zip -Force

# Jellyfin's catalog installer creates its own plugin directory, so this ZIP must be flat.
$catalogDirectory = Join-Path $PSScriptRoot 'packages'
New-Item -ItemType Directory -Force -Path $catalogDirectory | Out-Null
$catalogName = "EpisodeComments-jellyfin12-$shortVersion.zip"
$catalogZip = Join-Path $catalogDirectory $catalogName
Compress-Archive -LiteralPath @($dll, (Join-Path $PSScriptRoot 'meta.json'), (Join-Path $PSScriptRoot 'LICENSE')) -DestinationPath $catalogZip -Force
$previousVersions = @()
$manifestPath = Join-Path $PSScriptRoot 'manifest.json'
if (Test-Path -LiteralPath $manifestPath) {
    $previousCatalog = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    $previousVersions = @($previousCatalog | Where-Object { $_.guid -eq $metadata.guid } | ForEach-Object { $_.versions } | Where-Object { $_.version -ne $metadata.version })
}
$manifest = @(
    [ordered]@{
        guid = $metadata.guid
        name = $metadata.name
        overview = $metadata.overview
        description = $metadata.description
        owner = $metadata.owner
        category = $metadata.category
        versions = @(
            [ordered]@{
                version = $metadata.version
                changelog = 'Movie and series discussions, backwards-compatible episode API, and Android TV comment previews with a regular Cast row.'
                targetAbi = $metadata.targetAbi
                sourceUrl = "https://raw.githubusercontent.com/$Repository/$Branch/packages/$catalogName"
                # Jellyfin's catalog checksum field specifically requires MD5.
                checksum = (Get-FileHash -LiteralPath $catalogZip -Algorithm MD5).Hash.ToLowerInvariant()
                timestamp = (Get-Item -LiteralPath $dll).LastWriteTimeUtc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'")
            }
        ) + $previousVersions
    }
)
ConvertTo-Json -InputObject $manifest -Depth 8 | Set-Content -LiteralPath (Join-Path $PSScriptRoot 'manifest.json') -Encoding utf8
Write-Output "Jellyfin repository URL: https://raw.githubusercontent.com/$Repository/$Branch/manifest.json"
Get-FileHash -LiteralPath $catalogZip -Algorithm SHA256
