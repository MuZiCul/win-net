# WinNetFix 单文件发布脚本（仅生成安装版所需的 exe，不再提供便携版）
# 用法:  ./publish.ps1
# 产物:  publish\WinNetFix.exe  (供 Inno Setup 安装版编译)

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
    -o $out

if ($LASTEXITCODE -ne 0) {
    Write-Host "发布失败" -ForegroundColor Red
    exit $LASTEXITCODE
}

# 读取 csproj 版本号
$csproj = [xml](Get-Content (Join-Path $root "WinNetFix.csproj") -Raw)
$ver = $csproj.Project.PropertyGroup.Version
if ([string]::IsNullOrWhiteSpace($ver)) { $ver = "0.0.0" }

$exe = Join-Path $out "WinNetFix.exe"
$size = [math]::Round((Get-Item $exe).Length / 1MB, 1)
Write-Host "==> 发布完成 v$ver (${size} MB):"
Write-Host "    $exe"
Write-Host "    安装版请用 Inno Setup 编译 installer\WinNetFix.iss"
