# AUDIT W06 acceptance: force-terminate the process at each critical step of a relocation,
# restart, and check that the operation log alone determines the actual state.
#
# Only throwaway fixtures created and removed by this script are used. The fault-injection mode
# refuses paths that look like user data, and this harness never points it at any.

[CmdletBinding()]
param(
    [string]$ExePath = 'dist/AppAssetSentinel.App.exe'
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$exe = if ([IO.Path]::IsPathRooted($ExePath)) { $ExePath } else { Join-Path $repoRoot $ExePath }
if (-not (Test-Path $exe)) { throw "未找到可执行文件：$exe" }

function Remove-Safely([string]$path) {
    if (-not (Test-Path $path)) { return }
    Get-ChildItem $path -Recurse -Directory -Force -ErrorAction SilentlyContinue | Where-Object {
        ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0
    } | ForEach-Object { cmd /c rmdir "$($_.FullName)" 2>$null | Out-Null }
    Remove-Item $path -Recurse -Force -ErrorAction SilentlyContinue
}

$results = @()

foreach ($crashAt in @('Copying', 'Verified', 'Switched')) {
    $runId = [guid]::NewGuid().ToString('N').Substring(0, 8)
    $root = Join-Path $env:TEMP "sentinel_fixture_${runId}_$crashAt"
    $source = Join-Path $root 'models'
    $target = Join-Path $root 'vault\models\ollama'

    Remove-Safely $root
    New-Item -ItemType Directory -Path (Join-Path $source 'blobs') -Force | Out-Null
    1..5 | ForEach-Object {
        $bytes = New-Object byte[] (512 * $_)
        (New-Object Random $_).NextBytes($bytes)
        [IO.File]::WriteAllBytes((Join-Path $source "blobs\sha256-$($_.ToString('d3'))"), $bytes)
    }

    Write-Host "`n=== 崩溃点：$crashAt ===" -ForegroundColor Cyan

    # 1. Run with injection. A non-zero exit here is expected: the process was terminated.
    $p = Start-Process -FilePath $exe `
        -ArgumentList '--profile=r1', '--fault-inject', "--source=$source", "--target=$target", "--crash-at=$crashAt" `
        -PassThru -NoNewWindow -Wait
    Write-Host "  注入进程退出码：$($p.ExitCode)（0xC0000409 表示熔断终止，符合预期）"

    # 2. Restart: read the log and classify against the disk.
    $tasks = & $exe --operations 2>$null
    $taskLine = $tasks | Select-String -Pattern 'task=' | Select-Object -First 1
    $taskId = if ($taskLine) { ($taskLine.ToString() -split 'task=')[1].Trim() } else { '' }
    Write-Host "  记录中的任务：$taskId"

    # 3. Ask the recovery inspector what the truth is.
    $mcpRequest = '{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"sentinel_recovery","arguments":{"task_id":"' + $taskId + '"}}}'
    $response = $mcpRequest | & $exe --mcp 2>$null
    $parsed = $response | ConvertFrom-Json
    $assessment = $parsed.result.content[0].text | ConvertFrom-Json

    Write-Host "  记录状态     : $($assessment.logged_state)"
    Write-Host "  磁盘实际布局 : $($assessment.observed_layout)"
    Write-Host "  日志与磁盘一致: $($assessment.log_matches_disk)"
    Write-Host "  数据可确认   : $($assessment.data_accounted_for)"
    Write-Host "  结论         : $($assessment.conclusion)"
    foreach ($action in $assessment.safe_next_actions) { Write-Host "    · $action" -ForegroundColor DarkGray }

    $results += [PSCustomObject]@{
        CrashAt = $crashAt
        LoggedState = $assessment.logged_state
        Observed = $assessment.observed_layout
        DataAccountedFor = $assessment.data_accounted_for
        Consistent = $assessment.log_matches_disk
    }

    Remove-Safely $root

    # Remove this rehearsal's own operation record.
    $opDir = Join-Path $env:LOCALAPPDATA 'AppAssetSentinel\operations'
    Get-ChildItem $opDir -Filter '*.json' -ErrorAction SilentlyContinue | ForEach-Object {
        if ((Get-Content $_.FullName -Raw) -match 'fault-injection') { Remove-Item $_.FullName -Force }
    }
}

Write-Host "`n=== 汇总 ===" -ForegroundColor Yellow
$results | Format-Table -AutoSize

$allSafe = ($results | Where-Object { -not $_.DataAccountedFor }).Count -eq 0
Write-Host "所有崩溃点重启后都能确认数据位置：$allSafe" -ForegroundColor $(if ($allSafe) { 'Green' } else { 'Red' })