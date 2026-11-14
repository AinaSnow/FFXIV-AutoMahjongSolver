param([switch]$VerifyOnly)
$ErrorActionPreference = 'Stop'
$dependencyDirectory = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../artifacts/deucalion'))
$assets = @(
    @{ Name = 'deucalion.dll'; Url = 'https://github.com/ff14wed/deucalion/releases/download/1.5.1/deucalion.dll'; Hash = '4AC5D10790BAA319027108CB1AECFA74C4F8D0423464A31642B186FE507CA9B8' },
    @{ Name = 'deucalion-1.5.1-source.zip'; Url = 'https://github.com/ff14wed/deucalion/archive/refs/tags/1.5.1.zip'; Hash = '394C052E735C00891D1A6F391F244B2F7EC26AA3928BE4DD5B33E4053747E9AB' }
)
if (!$VerifyOnly) { New-Item -ItemType Directory -Force -Path $dependencyDirectory | Out-Null }
foreach ($asset in $assets) {
    $destination = Join-Path $dependencyDirectory $asset.Name
    if (!(Test-Path -LiteralPath $destination)) {
        if ($VerifyOnly) { throw 'Capture runtime missing. Run ./tools/install-deucalion.ps1 before building.' }
        Invoke-WebRequest -Uri $asset.Url -OutFile $destination -UseBasicParsing
    }
    $hasher = [Security.Cryptography.SHA256]::Create()
    $inputStream = [IO.File]::OpenRead($destination)
    try { $actualHash = [BitConverter]::ToString($hasher.ComputeHash($inputStream)).Replace('-', '') }
    finally { $inputStream.Dispose(); $hasher.Dispose() }
    if ($actualHash -ne $asset.Hash) {
        throw "Capture dependency checksum mismatch: $($asset.Name)"
    }
}
Add-Type -AssemblyName System.IO.Compression.FileSystem
$sourceArchive = [IO.Compression.ZipFile]::OpenRead((Join-Path $dependencyDirectory 'deucalion-1.5.1-source.zip'))
try {
    $licenseEntry = $sourceArchive.GetEntry('deucalion-1.5.1/LICENSE.md')
    if ($null -eq $licenseEntry) { throw 'Pinned source archive lacks LICENSE.md' }
    $reader = [IO.StreamReader]::new($licenseEntry.Open())
    try { $license = $reader.ReadToEnd() } finally { $reader.Dispose() }
    $licensePath = Join-Path $dependencyDirectory 'LICENSE.md'
    if ($VerifyOnly) {
        if (!(Test-Path -LiteralPath $licensePath) -or [IO.File]::ReadAllText($licensePath) -ne $license) {
            throw 'Capture license missing or changed. Run ./tools/install-deucalion.ps1.'
        }
    } else { [IO.File]::WriteAllText($licensePath, $license, [Text.UTF8Encoding]::new($false)) }
} finally { $sourceArchive.Dispose() }
Write-Host 'Deucalion 1.5.1 runtime, source and license verified.'
