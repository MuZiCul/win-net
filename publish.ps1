# WinNetFix 单文件发布脚本
# 用法:  ./publish.ps1
# 产物:  publish\WinNetFix.exe            (固定名，供自启/日常使用)
#        publish\WinNetFix-vX.Y.Z.exe     (带版本号，供发布归档/便携)
#        publish\WinNetFix-vX.Y.Z.zip     (便携版压缩包)

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
$verExe = Join-Path $out "WinNetFix-v$ver.exe"
$verZip = Join-Path $out "WinNetFix-v$ver.zip"

# 复制一份带版本号的文件（保留固定名供自启路径稳定）
Copy-Item $exe $verExe -Force

# 便携版 zip（内含固定名 WinNetFix.exe，解压后注册自启路径稳定）
if (Test-Path $verZip) { Remove-Item $verZip -Force }
$staging = Join-Path $env:TEMP "winnetfix-portable"
if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }
New-Item -ItemType Directory -Path $staging -Force | Out-Null
Copy-Item $exe (Join-Path $staging "WinNetFix.exe")
Compress-Archive -Path (Join-Path $staging "WinNetFix.exe") -DestinationPath $verZip -Force
Remove-Item $staging -Recurse -Force

$size = [math]::Round((Get-Item $exe).Length / 1MB, 1)
Write-Host "==> 发布完成 (${size} MB):"
Write-Host "    $exe"
Write-Host "    $verExe"
Write-Host "    $verZip"
