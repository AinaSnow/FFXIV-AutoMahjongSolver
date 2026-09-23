param([Parameter(Mandatory=$true)][string]$ZipPath, [Parameter(Mandatory=$true)][string]$RuntimePath)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::Open($ZipPath,[IO.Compression.ZipArchiveMode]::Update)
try {
    foreach ($name in @('deucalion.dll','deucalion-1.5.0-source.zip','LICENSE.md','NOTICE.md')) {
        $file = Join-Path $RuntimePath $name
        if (!(Test-Path -LiteralPath $file)) { throw "Capture package file missing: $file" }
        $entryName = 'capture-runtime/' + $name
        foreach ($entry in @($archive.Entries)) {
            if ($entry.FullName.Replace('\','/') -eq $entryName) { $entry.Delete() }
        }
        [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive,$file,$entryName) | Out-Null
    }
} finally { $archive.Dispose() }
