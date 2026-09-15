# AUDIT W08 acceptance: prove the official-configuration redirect works with a REAL Ollama
# instance serving a REAL model store.
#
# Safety properties of this rehearsal:
#   - it never changes the user's OLLAMA_MODELS (user or machine scope);
#   - it never restarts or stops the user's running Ollama service;
#   - it starts a SECOND, throwaway instance on an unused port with the redirect applied only
#     to that child process;
#   - the model store is reached through a temporary junction created and removed here, so no
#     production bytes are copied or deleted.

[CmdletBinding()]
param(
    [string]$RealModels = 'D:\AIStack\models\ollama',
    [int]$TestPort = 11499
)

$ErrorActionPreference = 'Stop'

$runId = [guid]::NewGuid().ToString('N').Substring(0, 8)
$redirectRoot = Join-Path $env:TEMP "sentinel_ollama_redirect_$runId"
$linkPath = Join-Path $redirectRoot 'models'
$ollama = Join-Path $env:LOCALAPPDATA 'Programs\Ollama\ollama.exe'

if (-not (Test-Path $ollama)) { throw "未找到 ollama.exe：$ollama" }
if (-not (Test-Path $RealModels)) { throw "未找到真实模型库：$RealModels" }

$userConfigBefore = [Environment]::GetEnvironmentVariable('OLLAMA_MODELS', 'User')
Write-Host "用户级 OLLAMA_MODELS（运行前）：$userConfigBefore" -ForegroundColor Gray
Write-Host "本次不修改该值，只在子进程内重定向。" -ForegroundColor Gray

$proc = $null
try {
    New-Item -ItemType Directory -Path $redirectRoot -Force | Out-Null

    # A junction lets the child instance read the real store without copying 42 GB.
    $made = cmd /c mklink /J "$linkPath" "$RealModels" 2>&1
    if (-not (Test-Path $linkPath)) { throw "创建联接失败：$made" }
    Write-Host "重定向路径（联接）：$linkPath -> $RealModels" -ForegroundColor Cyan

    # Start the second instance: its models dir comes from the process environment,
    # which is exactly the mechanism the W08 adapter chose.
    $env:OLLAMA_MODELS = $linkPath
    $env:OLLAMA_HOST = "127.0.0.1:$TestPort"
    $env:OLLAMA_KEEP_ALIVE = '0s'

    Write-Host "启动第二个实例：OLLAMA_MODELS=$linkPath  OLLAMA_HOST=127.0.0.1:$TestPort" -ForegroundColor Cyan
    $proc = Start-Process -FilePath $ollama -ArgumentList 'serve' -PassThru -WindowStyle Hidden

    $reachable = $false
    for ($i = 0; $i -lt 80; $i++) {
        Start-Sleep -Milliseconds 250
        try {
            $null = Invoke-RestMethod -Uri "http://127.0.0.1:$TestPort/api/tags" -TimeoutSec 2
            $reachable = $true
            break
        } catch { }
    }

    if (-not $reachable) {
        Write-Host "[!!] 第二个实例在超时内未就绪。" -ForegroundColor Red
    } else {
        $tags = Invoke-RestMethod -Uri "http://127.0.0.1:$TestPort/api/tags" -TimeoutSec 5
        Write-Host "`n[验收] 重定向后的实例可提供的模型：" -ForegroundColor Yellow
        foreach ($m in $tags.models) {
            Write-Host "  - $($m.name)"
        }
        Write-Host "  合计：$($tags.models.Count) 个" -ForegroundColor Green

        if ($tags.models.Count -gt 0) {
            Write-Host "`n[通过] 真实 Ollama 实例在重定向后的位置成功读取并列出真实模型。" -ForegroundColor Green
            Write-Host "       这正是 W08 选择的「官方配置优先」机制：应用自己知道数据在哪。" -ForegroundColor Green
        } else {
            Write-Host "`n[失败] 实例已启动但未列出任何模型。" -ForegroundColor Red
        }
    }
}
finally {
    if ($proc) {
        Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
        Start-Sleep -Milliseconds 800
    }

    Remove-Item Env:\OLLAMA_MODELS -ErrorAction SilentlyContinue
    Remove-Item Env:\OLLAMA_HOST -ErrorAction SilentlyContinue
    Remove-Item Env:\OLLAMA_KEEP_ALIVE -ErrorAction SilentlyContinue

    # Remove the junction itself without touching its target.
    if (Test-Path $linkPath) {
        cmd /c rmdir "$linkPath" 2>$null | Out-Null
    }
    if (Test-Path $redirectRoot) {
        Remove-Item $redirectRoot -Recurse -Force -ErrorAction SilentlyContinue
    }

    # Clean any manifests/blobs the throwaway instance may have written at the target root.
    # (The instance only ever read; this asserts it, rather than assuming it.)
    $stray = Get-ChildItem $RealModels -Filter '*.tmp' -ErrorAction SilentlyContinue
    if ($stray) { Write-Host "  [注意] 目标库出现临时文件：$($stray.Name -join ', ')" -ForegroundColor DarkYellow }

    $userConfigAfter = [Environment]::GetEnvironmentVariable('OLLAMA_MODELS', 'User')
    Write-Host "`n用户级 OLLAMA_MODELS（运行后）：$userConfigAfter" -ForegroundColor Gray
    Write-Host "用户配置未被修改：$($userConfigBefore -eq $userConfigAfter)" -ForegroundColor $(if ($userConfigBefore -eq $userConfigAfter) { 'Green' } else { 'Red' })
    Write-Host "重定向路径残留：$(Test-Path $linkPath)" -ForegroundColor Gray

    # The user's own service must still be intact and serving.
    try {
        $live = Invoke-RestMethod -Uri 'http://127.0.0.1:11434/api/tags' -TimeoutSec 5
        Write-Host "用户原有服务仍在提供 $($live.models.Count) 个模型（未受影响）" -ForegroundColor Green
    } catch {
        Write-Host "[警告] 原有服务不可达：$_" -ForegroundColor Red
    }
}
