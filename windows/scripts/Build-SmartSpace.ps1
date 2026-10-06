param(
    [string]$Dotnet = 'dotnet',
    [string]$CacheDirectory = (Join-Path $env:LOCALAPPDATA 'Amanu SmartSpace Build')
)
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$output = Join-Path $repo 'windows/artifacts/smartspace-portable'
$cache = [IO.Path]::GetFullPath($CacheDirectory)
New-Item -ItemType Directory -Path $cache, $output -Force | Out-Null
& $Dotnet test (Join-Path $repo 'windows/Amanu.Windows.slnx') -c Release --nologo
if ($LASTEXITCODE -ne 0) { throw 'Tests failed' }
& $Dotnet publish (Join-Path $repo 'windows/src/Amanu.App/Amanu.App.csproj') -c Release -r win-x64 --self-contained true -o $output --nologo
if ($LASTEXITCODE -ne 0) { throw 'Publish failed' }
$archive = Join-Path $cache 'upstream-windows-0.6.6.zip'
if (-not (Test-Path -LiteralPath $archive)) {
    Invoke-WebRequest -Uri 'https://github.com/gsamat/amanu/releases/download/windows-v0.6.6/Amanu-stable-Portable.zip' -OutFile $archive
}
$expected = '13affe923a889b3b2faeb5bc300a864e5bf0249de85fb042593fbd640be485f9'
if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant() -ne $expected) { throw 'Upstream checksum mismatch' }
# Copy only unchanged runtimes/licenses; never copy the upstream launcher/updater.
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [IO.Compression.ZipFile]::OpenRead($archive)
try {
    foreach ($entry in $zip.Entries) {
        if ($entry.FullName -notmatch '^current/(?<relative>(?:local-runtime|live-runtime|Licenses)/.+)$') { continue }
        $target = [IO.Path]::GetFullPath((Join-Path $output $Matches.relative))
        if (-not $target.StartsWith([IO.Path]::GetFullPath($output) + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe ZIP entry' }
        if ($entry.Name.Length -eq 0) { New-Item -ItemType Directory -Path $target -Force | Out-Null; continue }
        New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($target)) -Force | Out-Null
        [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $target, $true)
    }
}
finally { $zip.Dispose() }
& (Join-Path $repo 'windows/tests/Test-NativeUnicodePaths.ps1') -RuntimeDirectory (Join-Path $output 'local-runtime')
Copy-Item -LiteralPath (Join-Path $repo 'LICENSE') -Destination (Join-Path $output 'LICENSE')
Copy-Item -LiteralPath (Join-Path $repo 'SMARTSPACE.md') -Destination (Join-Path $output 'README-SmartSpace.md')
Copy-Item -LiteralPath (Join-Path $repo 'tools/meeting_tracker_drafts.py') -Destination (Join-Path $output 'meeting_tracker_drafts.py')
$destination = Join-Path $repo 'windows/artifacts/Amanu-SmartSpace-candidate-win-x64.zip'
if (Test-Path -LiteralPath $destination) { throw "An existing candidate is never overwritten: $destination" }
[IO.Compression.ZipFile]::CreateFromDirectory($output, $destination, [IO.Compression.CompressionLevel]::Optimal, $false)
Get-FileHash -LiteralPath $destination -Algorithm SHA256
