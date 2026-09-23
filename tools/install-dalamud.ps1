param([string]$Destination = "$env:APPDATA/XIVLauncher/addon/Hooks/dev")
$ErrorActionPreference = 'Stop'
$lock = Get-Content -Raw -LiteralPath "$PSScriptRoot/../build/dependencies.lock.json" | ConvertFrom-Json
$archive = Join-Path ([IO.Path]::GetTempPath()) ("dalamud-" + $lock.distribution_commit + '.zip')
if (-not (Test-Path -LiteralPath $archive)) { Invoke-WebRequest -Uri $lock.url -OutFile $archive }
if ((Get-FileHash -Algorithm SHA256 -LiteralPath $archive).Hash -ne $lock.archive_sha256) { throw 'Dalamud archive checksum mismatch' }
New-Item -ItemType Directory -Force -Path $Destination | Out-Null
Expand-Archive -LiteralPath $archive -DestinationPath $Destination -Force
foreach ($file in $lock.files.PSObject.Properties) {
    if ((Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path $Destination $file.Name)).Hash -ne $file.Value) {
        throw "Dalamud dependency checksum mismatch: $($file.Name)"
    }
}
Write-Host "Verified Dalamud $($lock.dalamud_version), commit $($lock.dalamud_commit)"
