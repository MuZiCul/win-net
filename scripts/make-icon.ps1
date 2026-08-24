# 从 assets/WinNetFix.png 生成多尺寸 assets/WinNetFix.ico
# 用途：exe 应用图标（ApplicationIcon）+ 托盘图标（ExtractIconEx 提取）
# 用法：powershell -ExecutionPolicy Bypass -File scripts/make-icon.ps1
$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName System.Drawing

$root = Split-Path -Parent $PSScriptRoot
$pngPath = Join-Path $root 'assets\WinNetFix.png'
$icoPath = Join-Path $root 'assets\WinNetFix.ico'

$src = [System.Drawing.Image]::FromFile($pngPath)
try {
    # 目标尺寸（ICO 目录项 width/height 为 0 表示 256）
    $sizes = @(16, 32, 48, 256)

    # 缩放到各尺寸并编码为 PNG
    $pngCodec = [System.Drawing.Imaging.ImageCodecInfo]::GetImageEncoders() |
        Where-Object { $_.MimeType -eq 'image/png' }
    $encParams = New-Object System.Drawing.Imaging.EncoderParameters(1)
    $encParams.Param[0] = New-Object System.Drawing.Imaging.EncoderParameter(
        [System.Drawing.Imaging.Encoder]::ColorDepth, 32L)

    $pngBlobs = @()
    foreach ($s in $sizes) {
        $bmp = New-Object System.Drawing.Bitmap($s, $s)
        try {
            $g = [System.Drawing.Graphics]::FromImage($bmp)
            try {
                $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
                $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
                $g.Clear([System.Drawing.Color]::Transparent)
                $g.DrawImage($src, 0, 0, $s, $s)
            } finally { $g.Dispose() }
            $ms = New-Object System.IO.MemoryStream
            $bmp.Save($ms, $pngCodec, $encParams)
            $pngBlobs += , $ms.ToArray()
            $ms.Dispose()
        } finally { $bmp.Dispose() }
    }

    # 组装 ICO 容器（ICONDIR + ICONDIRENTRY[] + PNG 数据）
    $fs = [System.IO.File]::Create($icoPath)
    try {
        $bw = New-Object System.IO.BinaryWriter($fs)
        $bw.Write([uint16]0)            # reserved
        $bw.Write([uint16]1)            # type: icon
        $bw.Write([uint16]$sizes.Count) # image count
        $offset = 6 + 16 * $sizes.Count
        for ($i = 0; $i -lt $sizes.Count; $i++) {
            $s = $sizes[$i]
            $w = if ($s -ge 256) { 0 } else { $s }
            $bw.Write([byte]$w)        # width  (0=256)
            $bw.Write([byte]$w)        # height
            $bw.Write([byte]0)         # palette
            $bw.Write([byte]0)         # reserved
            $bw.Write([uint16]1)       # planes
            $bw.Write([uint16]32)      # bpp
            $bw.Write([uint32]$pngBlobs[$i].Length)
            $bw.Write([uint32]$offset)
            $offset += $pngBlobs[$i].Length
        }
        foreach ($blob in $pngBlobs) { $bw.Write($blob) }
        $bw.Flush()
    } finally { $fs.Dispose() }

    Write-Host "OK: $icoPath ($((Get-Item $icoPath).Length) bytes, $($sizes -join '/') px)"
} finally {
    $src.Dispose()
}
