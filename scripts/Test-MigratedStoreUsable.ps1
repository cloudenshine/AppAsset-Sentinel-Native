# AUDIT W08 final acceptance, without touching production data.
#
# Chain: real model store skeleton -> W07 kernel relocation across physical disks
#        -> real Ollama reads the CONFIRMED TARGET.
#
# Only manifests and small metadata blobs are copied (~25 KB). The multi-GB weights are
# deliberately excluded, so this proves a migrated store stays structurally usable and listable
# by the real application. Inference needs the weights and is a separate authorised step.

[CmdletBinding()]
param(
    [string]$RealModels = 'D:\AIStack\models\ollama',
    [string]$SourceDrive = 'D',
    [string]$TargetDrive = 'E',
    [string]$ExePath = 'dist/AppAssetSentinel.App.exe',
    [int]$TestPort = 11498
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$exe = if ([IO.Path]::IsPathRooted($ExePath)) { $ExePath } else { Join-Path $repoRoot $ExePath }
if (-not (Test-Path $exe)) { throw "not found: $exe" }

function Remove-Safely([string]$path) {
    if (-not (Test-Path $path)) { return }
    Get-ChildItem $path -Recurse -Directory -Force -ErrorAction SilentlyContinue | Where-Object {
        ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0
    } | ForEach-Object { cmd /c rmdir "$($_.FullName)" 2>$null | Out-Null }
    Remove-Item $path -Recurse -Force -ErrorAction SilentlyContinue
}

$runId = [guid]::NewGuid().ToString('N').Substring(0, 8)
$fixture = "${SourceDrive}:\sentinel_fixture_store_$runId"
$source = Join-Path $fixture 'ollama'
$vaultRoot = "${TargetDrive}:\sentinel_fixture_vault_$runId"
$target = Join-Path $vaultRoot 'AIStack\models\ollama'
Remove-Safely $fixture
Remove-Safely $vaultRoot

New-Item -ItemType Directory -Path (Join-Path $source 'manifests') -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $source 'blobs') -Force | Out-Null
Copy-Item (Join-Path $RealModels 'manifests\*') (Join-Path $source 'manifests') -Recurse -Force
Get-ChildItem (Join-Path $RealModels 'blobs') -File | Where-Object { $_.Length -lt 1MB } |
    ForEach-Object { Copy-Item $_.FullName (Join-Path $source 'blobs') -Force }

$srcDisk = (Get-Partition -DriveLetter $SourceDrive | Get-Disk).Number
$dstDisk = (Get-Partition -DriveLetter $TargetDrive | Get-Disk).Number
Write-Host "fixture: $((Get-ChildItem $source -Recurse -File | Measure-Object).Count) files" -ForegroundColor Cyan
Write-Host "disks: Disk$srcDisk -> Disk$dstDisk (cross=$($srcDisk -ne $dstDisk))" -ForegroundColor Cyan

$app = Start-Process -FilePath $exe -ArgumentList '--server-only','--profile=r1' -PassThru -WindowStyle Hidden
$probe = $null
try {
    for ($i=0; $i -lt 80; $i++) {
        if ((& curl.exe -s -o NUL -w '%{http_code}' 'http://127.0.0.1:8765/api/ready' 2>$null) -eq '200') { break }
        Start-Sleep -Milliseconds 250
    }
    $token = (& curl.exe -s 'http://127.0.0.1:8765/api/session' | ConvertFrom-Json).token
    $headers = @('-H','Content-Type: application/json','-H','Origin: http://127.0.0.1:8765','-H',"X-Sentinel-Session: $token")

    $bodyFile = Join-Path $env:TEMP "w08_body_$runId.json"
    (@{ source_path = $source; target_vault_path = $target; asset_name = 'ollama'; category = 'ai_models' } |
        ConvertTo-Json -Compress) | Set-Content -Path $bodyFile -Encoding ascii

    Write-Host "`n[1] kernel relocation across physical disks" -ForegroundColor Yellow
    $res = & curl.exe -s -X POST 'http://127.0.0.1:8765/api/vault/relocate' @headers --data-binary "@$bodyFile" | ConvertFrom-Json
    Remove-Item $bodyFile -Force -ErrorAction SilentlyContinue
    Write-Host "    status=$($res.status) code=$($res.code) did_mutate=$($res.did_mutate)"
    $manifestCount = (Get-ChildItem (Join-Path $target 'manifests') -Recurse -File -ErrorAction SilentlyContinue | Measure-Object).Count
    Write-Host "    confirmed target exists: $(Test-Path $target)   manifests moved: $manifestCount"
    if ($res.status -ne 'Succeeded') { throw "relocation failed: $($res.status) / $($res.code)" }

    Write-Host "`n[2] real Ollama reads the migrated target" -ForegroundColor Yellow
    $ollama = Join-Path $env:LOCALAPPDATA 'Programs\Ollama\ollama.exe'
    $env:OLLAMA_MODELS = $target
    $env:OLLAMA_HOST = "127.0.0.1:$TestPort"
    $env:OLLAMA_KEEP_ALIVE = '0s'
    $probe = Start-Process -FilePath $ollama -ArgumentList 'serve' -PassThru -WindowStyle Hidden

    $reachable = $false
    for ($i=0; $i -lt 80; $i++) {
        Start-Sleep -Milliseconds 250
        try { $null = Invoke-RestMethod -Uri "http://127.0.0.1:$TestPort/api/tags" -TimeoutSec 2; $reachable = $true; break } catch { }
    }

    if ($reachable) {
        $tags = Invoke-RestMethod -Uri "http://127.0.0.1:$TestPort/api/tags" -TimeoutSec 5
        Write-Host "    models listed after migration: $($tags.models.Count)" -ForegroundColor Green
        foreach ($m in $tags.models) { Write-Host "      - $($m.name)" }
    } else {
        Write-Host "    [FAIL] instance not ready; migrated usability unconfirmed" -ForegroundColor Red
    }

    Write-Host "`n[3] source-side assertions" -ForegroundColor Yellow
    $anchorIsLink = ((Get-Item $source -Force -ErrorAction SilentlyContinue).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0
    Write-Host "    anchor is now a reparse point: $anchorIsLink"
    $ops = & curl.exe -s 'http://127.0.0.1:8765/api/operations' | ConvertFrom-Json
    $rec = $ops.records | Select-Object -First 1
    Write-Host "    operation record: state=$($rec.state) disposition=$($rec.backup_disposition)"
    Write-Host "    source backup retained: $(Test-Path $rec.source_backup_path)"
}
finally {
    if ($probe) { Stop-Process -Id $probe.Id -Force -ErrorAction SilentlyContinue }
    Remove-Item Env:\OLLAMA_MODELS -ErrorAction SilentlyContinue
    Remove-Item Env:\OLLAMA_HOST -ErrorAction SilentlyContinue
    Remove-Item Env:\OLLAMA_KEEP_ALIVE -ErrorAction SilentlyContinue
    Stop-Process -Id $app.Id -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 800
    Remove-Safely $fixture
    Remove-Safely $vaultRoot

    $opDir = Join-Path $env:LOCALAPPDATA 'AppAssetSentinel\operations'
    Get-ChildItem $opDir -Filter '*.json' -ErrorAction SilentlyContinue | ForEach-Object {
        if ((Get-Content $_.FullName -Raw) -match 'sentinel_fixture_store_') { Remove-Item $_.FullName -Force }
    }

    Write-Host "`nuser-level OLLAMA_MODELS: $([Environment]::GetEnvironmentVariable('OLLAMA_MODELS','User'))" -ForegroundColor Gray
    Write-Host "residue: fixture=$(Test-Path $fixture) target=$(Test-Path $vaultRoot)" -ForegroundColor Gray
}
