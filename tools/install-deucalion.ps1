param([switch]$VerifyOnly)
$ErrorActionPreference = 'Stop'
$dependencyDirectory = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../artifacts/deucalion'))
$assets = @(
    @{ Name = 'deucalion.dll'; Url = 'https://github.com/ff14wed/deucalion/releases/download/1.5.0/deucalion.dll'; Hash = '326BE4DB261064EBB51B8C46D2940F55701D79887E2C65070AFF00DE4C8C65EF' },
    @{ Name = 'deucalion-1.5.0-source.zip'; Url = 'https://github.com/ff14wed/deucalion/archive/refs/tags/1.5.0.zip'; Hash = '07F664993A4A070CC44871752973A5B9C7632479750136EB5408F564A5C4D3EE' }
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
$sourceArchive = [IO.Compression.ZipFile]::OpenRead((Join-Path $dependencyDirectory 'deucalion-1.5.0-source.zip'))
try {
    $licenseEntry = $sourceArchive.GetEntry('deucalion-1.5.0/LICENSE.md')
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
Write-Host 'Deucalion 1.5.0 runtime, source and license verified.'
