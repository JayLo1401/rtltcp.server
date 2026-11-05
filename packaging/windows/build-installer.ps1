<#
.SYNOPSIS
    Builds the Windows release (self-contained single file) and the Inno Setup installer.

.DESCRIPTION
    dotnet publish -r win-x64, then packaging/windows/setup.iss with Inno Setup 6.
    Output in packaging/windows/output/: setup.exe and version.json (update manifest).

.EXAMPLE
    ./packaging/windows/build-installer.ps1 -Version 0.2.0 -BaseUrl https://repository.jaycloud.at/repository/rtltcp.server
#>
param(
    [Parameter(Mandatory)] [string]$Version,
    [string]$BaseUrl = ''
)

$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$here = $PSScriptRoot
$publish = Join-Path $root 'artifacts\publish\win-x64'
$output = Join-Path $here 'output'

Remove-Item $publish, $output -Recurse -Force -ErrorAction SilentlyContinue
dotnet publish (Join-Path $root 'src\RtlTcp.Server') -c Release -r win-x64 "-p:Version=$Version" -o $publish
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)" }
foreach ($f in 'rtltcp-server.exe', 'libusb-1.0.dll') {
    if (-not (Test-Path (Join-Path $publish $f))) { throw "$f missing in publish output" }
}

$iscc = 'C:\Program Files (x86)\Inno Setup 6\ISCC.exe'
if (-not (Test-Path $iscc)) { $iscc = (Get-Command iscc -ErrorAction Stop).Source }
& $iscc (Join-Path $here 'setup.iss') "/DPublishDir=$publish" "/DAppVersion=$Version" /Q
if ($LASTEXITCODE -ne 0) { throw "Inno Setup failed ($LASTEXITCODE)" }

$setup = Join-Path $output 'setup.exe'
$file = "rtltcp-server-setup-$Version.exe"
[ordered]@{
    version = $Version
    url     = if ($BaseUrl) { "$BaseUrl/windows/release/$Version/$file" } else { '' }
    sha256  = (Get-FileHash -Algorithm SHA256 $setup).Hash.ToLower()
} | ConvertTo-Json | Set-Content -Encoding utf8 (Join-Path $output 'version.json')
Write-Host "Installer: $setup ($Version)"
