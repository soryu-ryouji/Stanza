# Stanza 发布脚本：生成单文件 exe
#
# 用法：
#   .\publish.ps1                 # 依赖本机 .NET 8 桌面运行时，体积最小（默认）
#   .\publish.ps1 -SelfContained  # 自包含 win-x64（免安装，推荐分发）
#   .\publish.ps1 -Runtime win-arm64   # ARM64 设备
#   .\publish.ps1 -SkipTests           # 跳过测试直接发布
[CmdletBinding()]
param(
    [ValidateSet("win-x64", "win-arm64")]
    [string]$Runtime = "win-x64",

    [switch]$SelfContained,

    [switch]$SkipTests
)

$ErrorActionPreference = "Stop"

$root    = Split-Path -Parent $MyInvocation.MyCommand.Path
$project = Join-Path $root "src/Stanza.App/Stanza.App.csproj"
$outDir  = Join-Path $root "publish/$Runtime"

# ---- 1. 测试（发布前置检查） ----
if (-not $SkipTests) {
    Write-Host "==> 运行单元测试..."
    dotnet test (Join-Path $root "tests/Stanza.Core.Tests") -c Release --nologo
    if ($LASTEXITCODE -ne 0) { throw "测试失败，中止发布。" }
}

# ---- 2. 清理并发布 ----
if (Get-Process -Name "Stanza" -ErrorAction SilentlyContinue) {
    throw "检测到 Stanza 正在运行，exe 文件被占用。请先关闭应用再发布。"
}
if (Test-Path $outDir) { Remove-Item $outDir -Recurse -Force }

$selfContained = $SelfContained.IsPresent
Write-Host "==> 发布 $Runtime（SelfContained = $selfContained）..."

# 压缩与自解压参数仅适用于自包含发布，否则报 NETSDK1176
$publishArgs = @(
    $project,
    "-c", "Release",
    "-r", $Runtime,
    "--self-contained", "$selfContained",
    "--nologo",
    "-p:PublishSingleFile=true",
    "-o", $outDir
)
if ($selfContained) {
    $publishArgs += "-p:IncludeNativeLibrariesForSelfExtract=true"
    $publishArgs += "-p:EnableCompressionInSingleFile=true"
}

dotnet publish @publishArgs
if ($LASTEXITCODE -ne 0) { throw "发布失败。" }

# ---- 3. 输出结果 ----
$exe = Join-Path $outDir "Stanza.exe"
if (-not (Test-Path $exe)) { throw "未找到输出文件：$exe" }

$size = "{0:N1} MB" -f ((Get-Item $exe).Length / 1MB)
Write-Host ""
Write-Host "发布完成：$exe（$size）" -ForegroundColor Green
if ($selfContained) {
    Write-Host "已内置 .NET 运行时，拷贝单个 exe 即可在任意 Windows 机器运行。"
} else {
    Write-Host "目标机器需安装 .NET 8 桌面运行时：https://dotnet.microsoft.com/download/dotnet/8.0"
}
