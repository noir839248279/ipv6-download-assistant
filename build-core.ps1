param(
    [string]$Go = 'go',
    [Parameter(Mandatory=$true)][string]$SourceArchive,
    [string]$Output = (Join-Path $PSScriptRoot 'work/core-build/mihomo.exe')
)
$ErrorActionPreference = 'Stop'
$source = (Resolve-Path -LiteralPath $SourceArchive).Path
$outputPath = [IO.Path]::GetFullPath($Output)
$stage = Join-Path $PSScriptRoot ('work/core-build/source-' + [Guid]::NewGuid().ToString('N'))
Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::ExtractToDirectory($source, $stage)
$root = Join-Path $stage 'mihomo-1.19.10'
if (!(Test-Path -LiteralPath (Join-Path $root 'go.mod'))) { throw 'Expected the official Mihomo v1.19.10 source archive.' }
# Prevent git apply from treating this nested source as part of the outer repo
# and silently skipping a patch whose paths are outside that repo prefix.
& git -C $root init --quiet
if ($LASTEXITCODE -ne 0) { throw 'Could not isolate the extracted core source.' }
$patch = Join-Path $PSScriptRoot 'third-party/mihomo-original-destination.patch'
& git -C $root apply --check $patch
if ($LASTEXITCODE -ne 0) { throw 'Core patch does not match the supplied upstream source.' }
& git -C $root apply $patch
if ($LASTEXITCODE -ne 0) { throw 'Core patch failed.' }
if (!(Get-Content -LiteralPath (Join-Path $root 'adapter/outbound/direct.go') -Raw).Contains('metadata = preserveDirectDestination(metadata)')) {
    throw 'Core patch was skipped; refusing to label an unpatched binary as fixed.'
}
foreach ($name in @('mihomo-original-destination.go', 'mihomo-original-destination_test.go')) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot "third-party/$name") -Destination (Join-Path $root "adapter/outbound/$name")
}
$saved = @{}
foreach ($name in @('CGO_ENABLED','GOOS','GOARCH','GOAMD64','GOTOOLCHAIN','GOMAXPROCS')) {
    $saved[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
}
try {
    $env:CGO_ENABLED='0'; $env:GOOS='windows'; $env:GOARCH='amd64'; $env:GOAMD64='v1'; $env:GOTOOLCHAIN='local'; $env:GOMAXPROCS='2'
    Push-Location $root
    try {
        & $Go test -p 2 -tags with_gvisor ./adapter/outbound -run '^TestIPv6LensPreserveDirectDestination$' -count=1
        if ($LASTEXITCODE -ne 0) { throw 'Core destination-preservation tests failed.' }
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($outputPath)) | Out-Null
        & $Go build -p 2 -tags with_gvisor -trimpath -ldflags '-X github.com/metacubex/mihomo/constant.Version=v1.19.10-ipv6lens.1 -s -w' -o $outputPath .
        if ($LASTEXITCODE -ne 0) { throw 'Core build failed.' }
    } finally { Pop-Location }
} finally {
    foreach ($name in $saved.Keys) { [Environment]::SetEnvironmentVariable($name,$saved[$name],'Process') }
}
& $outputPath -v
if ($LASTEXITCODE -ne 0) { throw 'Built core could not report its version.' }
Write-Output $outputPath
