<#
.SYNOPSIS
    Signs one file with Azure Artifact Signing, unless it already carries a valid signature.

.DESCRIPTION
    vpk pack runs this for every binary it signs (--signTemplate), and for Setup.exe.

    Why not vpk's own --azureTrustedSignFile: vpk 1.2.161 signs every binary, so the Windows App
    SDK's DLLs lost Microsoft's signature and carried ours instead (26 of them, measured
    2026-11-10). Velopack 1.3 leaves signed files alone by itself; once the release workflow moves
    to it, drop this script and pass --azureTrustedSignFile tools\signing.json again.

    It signs with the signtool.exe and Artifact Signing plug-in that vpk ships (vendor\signing in
    the vpk tool), so a build machine needs nothing besides vpk. The plug-in signs as whoever
    DefaultAzureCredential finds: `az login` on a PC, azure/login in GitHub Actions. That identity
    needs the Artifact Signing Certificate Profile Signer role on the profile in signing.json.
#>
param(
    [Parameter(Mandatory)]
    [string] $Path
)

$ErrorActionPreference = 'Stop'

if ((Get-AuthenticodeSignature -LiteralPath $Path).Status -eq 'Valid') {
    Write-Host "Already signed, left alone: $(Split-Path $Path -Leaf)"
    exit 0
}

# vpk's tool store: ~\.dotnet\tools\.store\vpk\<version>\vpk\<version>\vendor\signing.
$store = Join-Path $HOME '.dotnet\tools\.store\vpk'
$signing = Get-ChildItem $store -Directory -ErrorAction SilentlyContinue |
    Sort-Object { [version]($_.Name -replace '-.*$', '') } -Descending |
    ForEach-Object { Join-Path $_.FullName "vpk\$($_.Name)\vendor\signing" } |
    Where-Object { Test-Path (Join-Path $_ 'signtool.exe') } |
    Select-Object -First 1
if (-not $signing) {
    Write-Error "vpk's signtool.exe wasn't found under $store. Install vpk as a global tool."
}

& (Join-Path $signing 'signtool.exe') sign /fd SHA256 /tr http://timestamp.acs.microsoft.com /td SHA256 `
    /dlib (Join-Path $signing 'Azure.CodeSigning.Dlib.dll') /dmdf (Join-Path $PSScriptRoot 'signing.json') `
    $Path
exit $LASTEXITCODE
