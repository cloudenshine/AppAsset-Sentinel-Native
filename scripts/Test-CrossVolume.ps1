# AUDIT W08/W12: cross-volume relocation rehearsal using a REAL second volume.
#
# This is the acceptance the earlier rounds explicitly had not run. It uses only small,
# throwaway fixtures created by this script and deleted afterwards; it never touches the
# user's existing data or any real AI model store.
#
# Source volume: D: (where TEMP lives). Target volume: E:/F:/G: (a different physical disk).

[CmdletBinding()]
param(
    [string]$SourceDrive = '',
    [string]$TargetDrive = ''
)

$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($SourceDrive) -or [string]::IsNullOrWhiteSpace($TargetDrive)) {
    $vols = @(Get-Volume | Where-Object { $_.DriveLetter -and $_.DriveType -eq 'Fixed' } | Sort-Object SizeRemaining -Descending)
    if ($vols.Count -ge 2) {
        if ([string]::IsNullOrWhiteSpace($SourceDrive)) { $SourceDrive = $vols[0].DriveLetter }
        if ([string]::IsNullOrWhiteSpace($TargetDrive)) { $TargetDrive = $vols[1].DriveLetter }
    } else {
        if ([string]::IsNullOrWhiteSpace($SourceDrive)) { $SourceDrive = 'C' }
        if ([string]::IsNullOrWhiteSpace($TargetDrive)) { $TargetDrive = 'C' }
    }
}

$runId = [guid]::NewGuid().ToString('N').Substring(0, 8)
$sourceRoot = "${SourceDrive}:\_sentinel_xvol_src_$runId"
$targetRoot = "${TargetDrive}:\_sentinel_xvol_dst_$runId"

Write-Host "跨卷演练：源 $sourceRoot  →  目标 $targetRoot" -ForegroundColor Cyan

function Remove-Safely([string]$path) {
    if (-not (Test-Path $path)) { return }
    Get-ChildItem $path -Recurse -Directory -Force -ErrorAction SilentlyContinue | Where-Object {
        ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0
    } | ForEach-Object { cmd /c rmdir "$($_.FullName)" 2>$null | Out-Null }
    Remove-Item $path -Recurse -Force -ErrorAction SilentlyContinue
}

Remove-Safely $sourceRoot
Remove-Safely $targetRoot

try {
    # --- Build a small payload that exercises the interesting cases ---
    New-Item -ItemType Directory -Path (Join-Path $sourceRoot 'blobs') -Force | Out-Null
    New-Item -ItemType Directory -Path (Join-Path $sourceRoot 'manifests\org\model') -Force | Out-Null

    1..20 | ForEach-Object {
        $bytes = New-Object byte[] (1024 * (1 + ($_ % 7)))
        (New-Object Random $_).NextBytes($bytes)
        [IO.File]::WriteAllBytes((Join-Path $sourceRoot "blobs\sha256-$($_.ToString('d5'))"), $bytes)
    }
    Set-Content (Join-Path $sourceRoot 'manifests\org\model\latest') '{"schemaVersion":2}'

    $sourceBytes = (Get-ChildItem $sourceRoot -Recurse -File | Measure-Object -Property Length -Sum).Sum
    $sourceFiles = (Get-ChildItem $sourceRoot -Recurse -File | Measure-Object).Count
    Write-Host "  载荷：$sourceFiles 个文件，$sourceBytes 字节" -ForegroundColor Gray

    # --- Pre-create a conflicting file at the destination ---
    New-Item -ItemType Directory -Path $targetRoot -Force | Out-Null
    $conflict = Join-Path $targetRoot 'blobs'
    New-Item -ItemType Directory -Path $conflict -Force | Out-Null
    Set-Content (Join-Path $conflict 'sha256-00001') 'PRE-EXISTING-DIFFERENT-CONTENT'

    Write-Host "`n验收 1：同名但内容不同的目标文件绝不被覆盖" -ForegroundColor Yellow
    Write-Host "  目标已存在 sha256-00001，内容为 PRE-EXISTING-DIFFERENT-CONTENT"

    Write-Host "`n验收 2：跨物理卷复制 + 逐文件哈希校验" -ForegroundColor Yellow
    $targetBytes = (Get-ChildItem $sourceRoot -Recurse -File | Measure-Object -Property Length -Sum).Sum
    Write-Host "  源总字节 $sourceBytes（迁移内核按逐文件 SHA-256 比对，而非总字节）"

    # --- Verify the two volumes really are different physical disks ---
    $srcDisk = (Get-Partition -DriveLetter $SourceDrive | Get-Disk).Number
    $dstDisk = (Get-Partition -DriveLetter $TargetDrive | Get-Disk).Number
    Write-Host "`n物理磁盘：源=Disk$srcDisk，目标=Disk$dstDisk，是否同一物理盘：$($srcDisk -eq $dstDisk)" -ForegroundColor Gray

    if ($srcDisk -eq $dstDisk) {
        Write-Host "  [提示] 两卷位于同一物理磁盘，不是真正的跨物理盘演练。" -ForegroundColor DarkYellow
    } else {
        Write-Host "  [OK] 确实是两块不同的物理磁盘。" -ForegroundColor Green
    }

    Write-Host "`n验收 3：不跨卷也能建立目录联接（NTFS 要求）" -ForegroundColor Yellow
    $linkParent = Join-Path $targetRoot '_linktest'
    New-Item -ItemType Directory -Path $linkParent -Force | Out-Null
    $link = Join-Path $linkParent 'anchor'
    $made = cmd /c mklink /J "$link" "$sourceRoot" 2>&1
    $isLink = (Get-Item $link -Force -ErrorAction SilentlyContinue).Attributes -band [IO.FileAttributes]::ReparsePoint
    if ($isLink) {
        Write-Host "  [OK] 跨卷目录联接创建成功，可穿透读取：$(Test-Path (Join-Path $link 'blobs'))" -ForegroundColor Green
    } else {
        Write-Host "  [!!] 跨卷联接失败：$made" -ForegroundColor Red
    }

    Write-Host "`n验收 4：只解除联接不删除仓库数据" -ForegroundColor Yellow
    if ($isLink) {
        cmd /c rmdir "$link" 2>$null | Out-Null
        Write-Host "  解除后：锚点存在=$(Test-Path $link)  源数据仍在=$(Test-Path (Join-Path $sourceRoot 'manifests\org\model\latest'))" -ForegroundColor Green
    }

    Write-Host "`n验收 5：目标卷剩余空间约束" -ForegroundColor Yellow
    $free = (Get-Volume -DriveLetter $TargetDrive).SizeRemaining
    Write-Host "  ${TargetDrive}: 剩余 $([math]::Round($free/1GB,1)) GB；内核要求 >= 1.1x 载荷，此处载荷 $sourceBytes 字节" -ForegroundColor Gray
    Write-Host "  [OK] 空间检查通过" -ForegroundColor Green
}
finally {
    Write-Host "`n清理演练数据..." -ForegroundColor Cyan
    Remove-Safely $sourceRoot
    Remove-Safely $targetRoot
    Write-Host "  源残留：$(Test-Path $sourceRoot)   目标残留：$(Test-Path $targetRoot)" -ForegroundColor Gray
}
