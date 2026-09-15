# AUDIT W08/W12 acceptance: drive the real migration kernel ACROSS physical disks.
#
# Uses the app's own HTTP API under --profile=r1. Fixtures are small and throwaway; the
# user's existing data and AI model stores are never touched.

[CmdletBinding()]
param(
    [string]$SourceDrive = 'D',
    [string]$TargetDrive = 'E',
    [string]$ExePath = 'dist/AppAssetSentinel.App.exe'
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$exe = if ([IO.Path]::IsPathRooted($ExePath)) { $ExePath } else { Join-Path $repoRoot $ExePath }
if (-not (Test-Path $exe)) { throw "未找到可执行文件：$exe" }

$runId = [guid]::NewGuid().ToString('N').Substring(0, 8)
$sourceDir = "${SourceDrive}:\_sentinel_kernel_src_$runId\models"
$targetDir = "${TargetDrive}:\_sentinel_kernel_dst_$runId\models\ollama"

function Remove-Safely([string]$path) {
    if (-not (Test-Path $path)) { return }
    Get-ChildItem $path -Recurse -Directory -Force -ErrorAction SilentlyContinue | Where-Object {
        ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0
    } | ForEach-Object { cmd /c rmdir "$($_.FullName)" 2>$null | Out-Null }
    Remove-Item $path -Recurse -Force -ErrorAction SilentlyContinue
}

$baseSource = Split-Path -Parent $sourceDir
$baseTarget = "${TargetDrive}:\_sentinel_kernel_dst_$runId"
Remove-Safely $baseSource
Remove-Safely $baseTarget
New-Item -ItemType Directory -Path $sourceDir -Force | Out-Null

# A believable small Ollama-shaped store.
New-Item -ItemType Directory -Path (Join-Path $sourceDir 'blobs') -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $sourceDir 'manifests\registry.ollama.ai\library\qwen') -Force | Out-Null
1..12 | ForEach-Object {
    $bytes = New-Object byte[] (2048 * $_)
    (New-Object Random $_).NextBytes($bytes)
    [IO.File]::WriteAllBytes((Join-Path $sourceDir "blobs\sha256-$($_.ToString('d12'))"), $bytes)
}
Set-Content (Join-Path $sourceDir 'manifests\registry.ollama.ai\library\qwen\latest') '{"schemaVersion":2}'

$srcFiles = (Get-ChildItem $sourceDir -Recurse -File | Measure-Object).Count
$srcBytes = (Get-ChildItem $sourceDir -Recurse -File | Measure-Object -Property Length -Sum).Sum
Write-Host "载荷：$srcFiles 文件 / $srcBytes 字节  ($sourceDir → $targetDir)" -ForegroundColor Cyan

$srcDisk = (Get-Partition -DriveLetter $SourceDrive | Get-Disk).Number
$dstDisk = (Get-Partition -DriveLetter $TargetDrive | Get-Disk).Number
Write-Host "物理磁盘：源 Disk$srcDisk → 目标 Disk$dstDisk（跨物理盘=$($srcDisk -ne $dstDisk)）" -ForegroundColor Cyan

$proc = Start-Process -FilePath $exe -ArgumentList '--server-only', '--profile=r1' -PassThru -WindowStyle Hidden
try {
    $ready = $false
    for ($i = 0; $i -lt 120; $i++) {
        $code = & curl.exe -s -o NUL -w '%{http_code}' 'http://127.0.0.1:8765/api/ready' 2>$null
        if ($code -eq '200') { $ready = $true; break }
        Start-Sleep -Milliseconds 250
    }
    if (-not $ready) { throw '应用未在超时内就绪。' }

    $session = (& curl.exe -s 'http://127.0.0.1:8765/api/session' | ConvertFrom-Json).token
    $headers = @(
        '-H', 'Content-Type: application/json',
        '-H', 'Origin: http://127.0.0.1:8765',
        '-H', "X-Sentinel-Session: $session"
    )

    $profile = (& curl.exe -s 'http://127.0.0.1:8765/api/policy' | ConvertFrom-Json).profile
    Write-Host "`n策略档位：$profile" -ForegroundColor Yellow

    # ---------- Case A: clean relocation across volumes ----------
    Write-Host "`n[CASE A] 跨卷迁移（无冲突）" -ForegroundColor Yellow
    $bodyA = @{ source_path = $sourceDir; target_vault_path = $targetDir; asset_name = 'ollama'; category = 'ai_models' } | ConvertTo-Json -Compress
    $fileA = Join-Path $env:TEMP "sentinel_xvol_a_$runId.json"
    Set-Content -Path $fileA -Value $bodyA -Encoding ascii
    $resA = & curl.exe -s -X POST 'http://127.0.0.1:8765/api/vault/relocate' @headers --data-binary "@$fileA" | ConvertFrom-Json
    Remove-Item $fileA -Force -ErrorAction SilentlyContinue

    Write-Host "  status=$($resA.status) code=$($resA.code) did_mutate=$($resA.did_mutate)"
    Write-Host "  确认目标路径存在：$(Test-Path $targetDir)"
    Write-Host "  未产生重复叶子层级（…/models/models）：$(Test-Path "${TargetDrive}:\_sentinel_kernel_dst_$runId\models\models")"
    $anchorIsLink = ((Get-Item $sourceDir -Force -ErrorAction SilentlyContinue).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0
    Write-Host "  锚点已是重解析点：$anchorIsLink"
    Write-Host "  穿透读取锚点：$(Test-Path (Join-Path $sourceDir 'manifests\registry.ollama.ai\library\qwen\latest'))"

    $ops = & curl.exe -s 'http://127.0.0.1:8765/api/operations' | ConvertFrom-Json
    $rec = $ops.records | Select-Object -First 1
    Write-Host "  操作记录：state=$($rec.state) disposition=$($rec.backup_disposition)"
    Write-Host "  源备份保留（空间未回收）：$(Test-Path $rec.source_backup_path)"

    # ---------- Case B: destination already holds a different file ----------
    Write-Host "`n[CASE B] 目标已存在同名但内容不同的文件" -ForegroundColor Yellow
    $sourceB = "${SourceDrive}:\_sentinel_kernel_src_${runId}b\models"
    $targetB = "${TargetDrive}:\_sentinel_kernel_dst_${runId}b\shared.bin"
    $targetBParent = "${TargetDrive}:\_sentinel_kernel_dst_${runId}b"
    New-Item -ItemType Directory -Path $sourceB -Force | Out-Null
    New-Item -ItemType Directory -Path $targetBParent -Force | Out-Null
    Set-Content (Join-Path $sourceB 'shared.bin') 'SOURCE-VERSION'
    Set-Content $targetB 'EXISTING-TARGET-VERSION'

    # The target here is the parent dir so the kernel compares shared.bin inside it.
    $bodyB = @{ source_path = $sourceB; target_vault_path = "${TargetDrive}:\_sentinel_kernel_dst_${runId}b"; asset_name = 'conflict'; category = 'ai_models' } | ConvertTo-Json -Compress
    $fileB = Join-Path $env:TEMP "sentinel_xvol_b_$runId.json"
    Set-Content -Path $fileB -Value $bodyB -Encoding ascii
    $resB = & curl.exe -s -X POST 'http://127.0.0.1:8765/api/vault/relocate' @headers --data-binary "@$fileB" | ConvertFrom-Json
    Remove-Item $fileB -Force -ErrorAction SilentlyContinue

    Write-Host "  status=$($resB.status) code=$($resB.code) did_mutate=$($resB.did_mutate)"
    Write-Host "  目标原文件未被覆盖：$(if (Test-Path $targetB) { (Get-Content $targetB -Raw).Trim() } else { '(缺失)' })"
    Write-Host "  源文件仍然存在：$(if (Test-Path (Join-Path $sourceB 'shared.bin')) { (Get-Content (Join-Path $sourceB 'shared.bin') -Raw).Trim() } else { '(缺失)' })"

    Remove-Safely ("${SourceDrive}:\_sentinel_kernel_src_${runId}b")
    Remove-Safely $targetBParent
}
finally {
    Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 500
    Remove-Safely $baseSource
    Remove-Safely $baseTarget

    # The app's own operation log is real state; clear the rehearsal records only.
    $opDir = Join-Path $env:LOCALAPPDATA 'AppAssetSentinel\operations'
    Get-ChildItem $opDir -Filter '*.json' -ErrorAction SilentlyContinue | ForEach-Object {
        $content = Get-Content $_.FullName -Raw
        if ($content -match '_sentinel_kernel_') { Remove-Item $_.FullName -Force }
    }

    Write-Host "`n清理：源残留=$(Test-Path $baseSource) 目标残留=$(Test-Path $baseTarget)" -ForegroundColor Cyan
}
