# Maintenance-window run: relocate the real Ollama model store through the W07 kernel using the
# official-configuration mechanism, then verify the application still works.
#
# Preconditions asserted here rather than assumed: Ollama must be stopped, the source must exist,
# and the target volume must have room for a full second copy plus staging.

[CmdletBinding()]
param(
    [string]$Source = 'D:\AIStack\models\ollama',
    [string]$Target = 'D:\AIStack_Vault\models\ollama',
    [string]$ExePath = 'dist/AppAssetSentinel.App.exe',
    [string]$LogPath = 'D:\AIStack_Vault\maintenance-migration.log'
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$exe = if ([IO.Path]::IsPathRooted($ExePath)) { $ExePath } else { Join-Path $repoRoot $ExePath }

function Say($m) {
    $line = "[{0:HH:mm:ss}] {1}" -f (Get-Date), $m
    Write-Host $line
    Add-Content -Path $LogPath -Value $line -Encoding utf8
}

New-Item -ItemType Directory -Path (Split-Path -Parent $LogPath) -Force -ErrorAction SilentlyContinue | Out-Null
Set-Content -Path $LogPath -Value "=== 真实迁移 $((Get-Date).ToString('s')) ===" -Encoding utf8

# --- preconditions ---
$ollamaProcs = @(Get-Process -Name '*ollama*' -ErrorAction SilentlyContinue)
if ($ollamaProcs.Count -gt 0) { throw "Ollama 仍在运行（$($ollamaProcs.Count) 个进程），拒绝迁移。" }
if (-not (Test-Path $Source)) { throw "源不存在：$Source" }

$srcBytes = (Get-ChildItem $Source -Recurse -File | Measure-Object -Property Length -Sum).Sum
$free = (Get-Volume -DriveLetter ([IO.Path]::GetPathRoot($Target).Substring(0,1))).SizeRemaining
if ($free -lt ($srcBytes * 2.2)) { throw "目标卷空间不足：需要约 $([math]::Round($srcBytes*2.2/1GB,1)) GB，实际 $([math]::Round($free/1GB,1)) GB" }

Say "源 $Source（$([math]::Round($srcBytes/1GB,2)) GB）"
Say "目标 $Target"
Say "前置检查通过：Ollama 未运行，源存在，空间充足"

# --- run ---
$app = Start-Process -FilePath $exe -ArgumentList '--server-only','--profile=r1' -PassThru -WindowStyle Hidden
try {
    $ready = $false
    for ($i = 0; $i -lt 120; $i++) {
        if ((& curl.exe -s -o NUL -w '%{http_code}' 'http://127.0.0.1:8765/api/ready' 2>$null) -eq '200') { $ready = $true; break }
        Start-Sleep -Milliseconds 250
    }
    if (-not $ready) { throw '应用未就绪。' }

    $token = (& curl.exe -s 'http://127.0.0.1:8765/api/session' | ConvertFrom-Json).token
    $headers = @('-H','Content-Type: application/json','-H','Origin: http://127.0.0.1:8765','-H',"X-Sentinel-Session: $token")

    $body = @{
        source_path = $Source
        target_vault_path = $Target
        asset_name = 'ollama'
        category = 'ai_models'
        mechanism = 'official_config'
        config_variable = 'OLLAMA_MODELS'
    } | ConvertTo-Json -Compress

    $bodyFile = Join-Path $env:TEMP "real_migration_body.json"
    Set-Content -Path $bodyFile -Value $body -Encoding ascii

    Say "调用内核（official_config，逐文件 SHA-256 校验，预计数分钟）"
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $res = & curl.exe -s --max-time 3600 -X POST 'http://127.0.0.1:8765/api/vault/relocate' @headers --data-binary "@$bodyFile" | ConvertFrom-Json
    $sw.Stop()
    Remove-Item $bodyFile -Force -ErrorAction SilentlyContinue

    Say "内核返回：$($res.status) / $($res.code)  用时 $([math]::Round($sw.Elapsed.TotalMinutes,1)) 分钟"
    Say "did_mutate = $($res.did_mutate)"
    foreach ($e in $res.evidence) { Say "  $e" }
    Say "message: $($res.message)"
    Say "recovery: $($res.recovery)"

    $ops = & curl.exe -s 'http://127.0.0.1:8765/api/operations' | ConvertFrom-Json
    $rec = $ops.records | Select-Object -First 1
    Say "记录：state=$($rec.state) disposition=$($rec.backup_disposition)"
    Say "备份路径：$($rec.source_backup_path)"

    Say "配置现值：$([Environment]::GetEnvironmentVariable('OLLAMA_MODELS','User'))"
    Say "目标内容：manifests=$((Get-ChildItem (Join-Path $Target 'manifests') -Recurse -File -ErrorAction SilentlyContinue | Measure-Object).Count) blobs=$((Get-ChildItem (Join-Path $Target 'blobs') -File -ErrorAction SilentlyContinue | Measure-Object).Count)"
}
finally {
    Stop-Process -Id $app.Id -Force -ErrorAction SilentlyContinue
    Say "迁移阶段结束"
}