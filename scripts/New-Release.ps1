# AUDIT W12: reproducible release packaging.
#
# The audit rejected treating a single EXE as the deliverable: the program looks for
# wwwroot/ and rules/ on disk, so a bare EXE cannot be inferred to work from a source-tree
# run. This script produces a complete, self-describing ZIP whose contents can be verified
# against the commit that produced them.
#
# Usage: pwsh -File scripts/New-Release.ps1 -Configuration Release -Runtime win-x64

[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$Runtime = 'win-x64',
    [string]$SdkRoot = $env:DOTNET_ROOT
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$appProject = Join-Path $repoRoot 'src/AppAssetSentinel.App/AppAssetSentinel.App.csproj'
$publishDir = Join-Path $repoRoot "artifacts/publish-$Runtime"
$packageDir = Join-Path $repoRoot "artifacts/package-$Runtime"

if (-not $SdkRoot) { throw '缺少 DOTNET_ROOT；请先设置 .NET SDK 路径。' }
$dotnet = Join-Path $SdkRoot 'dotnet.exe'
if (-not (Test-Path $dotnet)) { throw "未找到 dotnet：$dotnet" }

Write-Host "[1/6] 清理旧产物" -ForegroundColor Cyan
foreach ($dir in @($publishDir, $packageDir)) {
    if (Test-Path $dir) { Remove-Item $dir -Recurse -Force }
}
New-Item -ItemType Directory -Path $packageDir -Force | Out-Null

Write-Host "[2/6] 发布自包含单文件" -ForegroundColor Cyan
& $dotnet publish $appProject `
    -c $Configuration -r $Runtime --self-contained `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -o $publishDir --nologo -v quiet
if ($LASTEXITCODE -ne 0) { throw "发布失败，退出码 $LASTEXITCODE" }

Write-Host "[3/6] 组装完整分发包（含本地网页与规则）" -ForegroundColor Cyan
Copy-Item (Join-Path $publishDir 'AppAssetSentinel.App.exe') $packageDir -Force

# The program reads these from disk at runtime; a package without them is incomplete.
foreach ($required in @('wwwroot', 'rules')) {
    $source = Join-Path $publishDir $required
    if (-not (Test-Path $source)) { throw "发布结果缺少必需目录：$required" }
    Copy-Item $source (Join-Path $packageDir $required) -Recurse -Force
}

Copy-Item (Join-Path $repoRoot 'appasset-audit/RELEASE_v1.0.0-r0.md') $packageDir -Force
Copy-Item (Join-Path $repoRoot 'README.md') $packageDir -Force
Copy-Item (Join-Path $repoRoot 'LICENSE') $packageDir -Force

# Vendor assets must be present locally; a remote CDN reference is a release blocker.
$indexPath = Join-Path $packageDir 'wwwroot/index.html'
$indexHtml = Get-Content $indexPath -Raw
if ($indexHtml -match 'https://cdn\.tailwindcss\.com') {
    throw 'index.html 仍引用远程 CDN；AUDIT A08 要求本地静态资源。'
}
if (-not (Test-Path (Join-Path $packageDir 'wwwroot/vendor/tailwind.min.js'))) {
    throw '缺少本地前端资源 wwwroot/vendor/tailwind.min.js'
}

$commit = (& git -C $repoRoot rev-parse HEAD).Trim()

# AUDIT W12: the packaged documents must not contradict the machine-readable record.
# A summary once hardcoded a commit and shipped naming a different archive than the one beside
# it, so this now fails the build instead of trusting the prose to stay current.
$commitShort = $commit.Substring(0, 7)
$docs = @(Get-ChildItem -Path $packageDir -Filter *.md -File)
foreach ($doc in $docs) {
    $text = [System.IO.File]::ReadAllText($doc.FullName)

    foreach ($zipRef in [regex]::Matches($text, "AppAssetSentinelNative-win-x64-[0-9a-f]{7}\.zip")) {
        if ($zipRef.Value -ne "AppAssetSentinelNative-win-x64-$commitShort.zip") {
            throw "打包文档 $($doc.Name) 引用了与本包不符的分发包名：$($zipRef.Value)"
        }
    }

    foreach ($hashRef in [regex]::Matches($text, "[0-9a-f]{40}")) {
        if (-not $commit.StartsWith($hashRef.Value, [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "打包文档 $($doc.Name) 含有与本次构建不符的提交号：$($hashRef.Value)"
        }
    }
}

Write-Host "      文档与本包记录一致（提交 $commitShort）" -ForegroundColor DarkGray

Write-Host "[4/6] 生成清单（文件哈希 + 提交 + 运行时版本）" -ForegroundColor Cyan
$dirty = (& git -C $repoRoot status --porcelain)

$entries = Get-ChildItem $packageDir -Recurse -File | ForEach-Object {
    [PSCustomObject]@{
        path = $_.FullName.Substring($packageDir.Length + 1).Replace('\', '/')
        bytes = $_.Length
        sha256 = (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    }
} | Sort-Object path

$manifest = [PSCustomObject]@{
    package = "AppAssetSentinelNative-$Runtime"
    built_at_utc = (Get-Date).ToUniversalTime().ToString('o')
    commit = $commit
    working_tree_dirty = [bool]($dirty)
    configuration = $Configuration
    runtime = $Runtime
    dotnet_sdk = (& $dotnet --version)
    file_count = $entries.Count
    total_bytes = ($entries | Measure-Object -Property bytes -Sum).Sum
    files = $entries
}

$manifestPath = Join-Path $packageDir 'MANIFEST.json'
$manifest | ConvertTo-Json -Depth 5 | Set-Content $manifestPath -Encoding utf8

Write-Host "[5/6] 打包 ZIP" -ForegroundColor Cyan
$zipName = "AppAssetSentinelNative-$Runtime-$($commit.Substring(0,7)).zip"
$zipPath = Join-Path $repoRoot "artifacts/$zipName"
if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
Compress-Archive -Path (Join-Path $packageDir '*') -DestinationPath $zipPath -CompressionLevel Optimal

Write-Host "[6/6] 结果" -ForegroundColor Cyan
$zipHash = (Get-FileHash $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
Write-Host "  分发包目录 : $packageDir"
Write-Host "  ZIP        : $zipPath"
Write-Host "  ZIP SHA256 : $zipHash"
Write-Host "  提交       : $commit (dirty=$([bool]$dirty))"
Write-Host "  文件数     : $($entries.Count)，共 $($manifest.total_bytes) 字节"
Write-Host ""
Write-Host "验收提示：把 ZIP 解压到与源码无关的目录后运行 AppAssetSentinel.App.exe --server-only" -ForegroundColor Yellow
