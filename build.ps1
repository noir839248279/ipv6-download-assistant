param([string]$Dotnet = 'dotnet', [Parameter(Mandatory)][string]$Mihomo)
$ErrorActionPreference = 'Stop'
if (!(Test-Path -LiteralPath $Mihomo)) { throw 'Provide the private patched Mihomo binary with -Mihomo; see build-core.ps1.' }
$coreVersion = & $Mihomo -v
if ($LASTEXITCODE -ne 0 -or ($coreVersion -join "`n") -notmatch '^Mihomo Meta v1\.19\.10-ipv6lens\.2 windows amd64(?:\s|$)') {
    throw 'Build requires the patched v1.19.10-ipv6lens.2 Windows amd64 core, not an external proxy core.'
}
$env:HTTP_PROXY='http://127.0.0.1:7897'
$env:HTTPS_PROXY=$env:HTTP_PROXY
$env:ALL_PROXY=$env:HTTP_PROXY
$env:WS_PROXY=$env:HTTP_PROXY
$env:WSS_PROXY=$env:HTTP_PROXY
$env:NO_PROXY='localhost,127.0.0.1,::1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT='1'
$env:DOTNET_NOLOGO='1'
& $Dotnet publish (Join-Path $PSScriptRoot 'src/IPv6Lens.csproj') -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -o (Join-Path $PSScriptRoot 'app')
if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
Write-Output (Join-Path $PSScriptRoot 'app/IPv6Lens.exe')

New-Item -ItemType Directory -Force (Join-Path $PSScriptRoot 'app/core') | Out-Null
Copy-Item -LiteralPath $Mihomo -Destination (Join-Path $PSScriptRoot 'app/core/mihomo.exe')
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'src/assets/app.ico') -Destination (Join-Path $PSScriptRoot 'app/app.ico') -Force
