Add-Type -AssemblyName System.Drawing

$src  = "$PSScriptRoot\assets\logo.png"
$out  = "$PSScriptRoot\assets"

# --- logo 256x256 haute qualite pour l'UI (base64 leger)
$img = [System.Drawing.Image]::FromFile($src)
$small = New-Object System.Drawing.Bitmap 256, 256
$g = [System.Drawing.Graphics]::FromImage($small)
$g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
$g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
$g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
$g.DrawImage($img, 0, 0, 256, 256)
$g.Dispose()

$small.Save("$out\logo_small.png", [System.Drawing.Imaging.ImageFormat]::Png)
$small.Dispose(); $img.Dispose()

# --- ICO (PNG integre) depuis le logo 256
$bytes = [System.IO.File]::ReadAllBytes("$out\logo_small.png")
$fs = [System.IO.File]::Create("$PSScriptRoot\app.ico")
$bw = New-Object System.IO.BinaryWriter($fs)
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]1)
$bw.Write([byte]0); $bw.Write([byte]0); $bw.Write([byte]0); $bw.Write([byte]0)
$bw.Write([uint16]1); $bw.Write([uint16]32)
$bw.Write([uint32]$bytes.Length); $bw.Write([uint32]22)
$bw.Write($bytes)
$bw.Close()

"logo_small.png: $((Get-Item "$out\logo_small.png").Length) octets"
"app.ico: $((Get-Item "$PSScriptRoot\app.ico").Length) octets"
