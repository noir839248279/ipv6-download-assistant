param(
    [string]$Dotnet = 'dotnet',
    [Parameter(Mandatory)][string]$Mihomo,
    [Parameter(Mandatory)][string]$MihomoSource,
    [switch]$SkipBuild
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$version = ([xml](Get-Content -LiteralPath (Join-Path $PSScriptRoot 'src/IPv6Lens.csproj') -Raw)).Project.PropertyGroup.Version
$coreVersion = & $Mihomo -v
if ($LASTEXITCODE -ne 0 -or ($coreVersion -join "`n") -notmatch '^Mihomo Meta v1\.19\.10-ipv6lens\.1 windows amd64(?:\s|$)') {
    throw 'This package requires the patched Mihomo v1.19.10-ipv6lens.1 Windows amd64 binary.'
}
$source = [IO.Compression.ZipFile]::OpenRead((Resolve-Path -LiteralPath $MihomoSource))
try {
    if (!($source.Entries | Where-Object FullName -eq 'mihomo-1.19.10/LICENSE')) {
        throw 'Provide the official Mihomo v1.19.10 source archive.'
    }
} finally { $source.Dispose() }
if (!$SkipBuild) {
    & (Join-Path $PSScriptRoot 'build.ps1') -Dotnet $Dotnet -Mihomo $Mihomo
}
$exe = Join-Path $PSScriptRoot 'app/IPv6Lens.exe'
if ([Diagnostics.FileVersionInfo]::GetVersionInfo($exe).FileVersion -ne "$version.0") {
    throw 'The published executable does not match the source version.'
}
$name = "IPv6-Download-Assistant-$version-win-x64-portable"
$output = Join-Path $PSScriptRoot 'releases'
$stage = Join-Path $output "$name-$([Guid]::NewGuid().ToString('N'))"
$root = Join-Path $stage $name
New-Item -ItemType Directory -Path (Join-Path $root 'core'),(Join-Path $root 'third-party') -Force | Out-Null
# Use an explicit file list: local configs, reports and build outputs never enter the archive.
Copy-Item -LiteralPath $exe -Destination (Join-Path $root 'IPv6Lens.exe')
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'src/assets/app.ico') -Destination (Join-Path $root 'app.ico')
Copy-Item -LiteralPath $Mihomo -Destination (Join-Path $root 'core/mihomo.exe')
Copy-Item -LiteralPath $MihomoSource -Destination (Join-Path $root 'third-party/mihomo-v1.19.10-source.zip')
$sourceFiles = @('README.md','THIRD-PARTY-NOTICES.md','third-party/mihomo-LICENSE.txt',
    'build-core.ps1','third-party/mihomo-original-destination.patch',
    'third-party/mihomo-original-destination.go','third-party/mihomo-original-destination_test.go')
foreach ($file in $sourceFiles) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot $file) -Destination (Join-Path $root $file)
}
$archive = Join-Path $output "$name.zip"
if (Test-Path -LiteralPath $archive) { throw "Release archive already exists: $archive" }
[IO.Compression.ZipFile]::CreateFromDirectory($stage,$archive,[IO.Compression.CompressionLevel]::Optimal,$false)
$zip = [IO.Compression.ZipFile]::OpenRead($archive)
try {
    $expected = (@('IPv6Lens.exe','app.ico','core/mihomo.exe','third-party/mihomo-v1.19.10-source.zip') + $sourceFiles) | ForEach-Object { "$name/$_" }
    $actual = @($zip.Entries | Where-Object { $_.Name } | ForEach-Object { $_.FullName.Replace('\','/') })
    if (@(Compare-Object $expected $actual).Count -ne 0) { throw 'The archive does not match the release file list.' }
} finally { $zip.Dispose() }
Write-Output $archive
