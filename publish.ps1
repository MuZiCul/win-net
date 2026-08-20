# WinNetFix 单文件发布脚本
# 用法:  ./publish.ps1
# 产物:  publish\WinNetFix.exe  (self-contained, 免装 .NET)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$out = Join-Path $root "publish"
$rid = "win-x64"

Write-Host "==> 发布 WinNetFix (win-x64, single-file, self-contained)..."

dotnet publish (Join-Path $root "WinNetFix.csproj") `
    -c Release `
    -r $rid `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:PublishTrimmed=true `
    -p:TrimMode=partial `
    -o $out

if ($LASTEXITCODE -ne 0) {
    Write-Host "发布失败" -ForegroundColor Red
    exit $LASTEXITCODE
}

$exe = Join-Path $out "WinNetFix.exe"
$size = [math]::Round((Get-Item $exe).Length / 1MB, 1)
Write-Host "==> 发布完成: $exe (${size} MB)"
