$ErrorActionPreference='Stop'
Add-Type -AssemblyName System.Drawing
$folder=Join-Path (Split-Path -Parent $PSScriptRoot) 'Assets'
New-Item -ItemType Directory -Path $folder -Force | Out-Null
$images=@()
foreach($size in @(16,24,32,48,64,128,256)){
    $bitmap=[Drawing.Bitmap]::new($size,$size)
    $g=[Drawing.Graphics]::FromImage($bitmap);$g.SmoothingMode='AntiAlias'
    $g.ScaleTransform($size/256.0,$size/256.0)
    $g.Clear([Drawing.Color]::FromArgb(20,26,36))
    $pen=[Drawing.Pen]::new([Drawing.Color]::FromArgb(76,211,193),20);$pen.StartCap='Round';$pen.EndCap='Round';$pen.LineJoin='Round'
    $g.DrawLine($pen,44,177,212,177)
    $g.DrawLine($pen,66,177,66,107);$g.DrawBezier($pen,66,107,92,53,164,53,190,107);$g.DrawLine($pen,190,107,190,177)
    $pen.Width=10;$g.DrawLine($pen,97,103,97,173);$g.DrawLine($pen,128,92,128,173);$g.DrawLine($pen,159,103,159,173)
    $brush=[Drawing.SolidBrush]::new([Drawing.Color]::FromArgb(247,157,104));$g.FillEllipse($brush,174,35,42,42)
    $stream=[IO.MemoryStream]::new();$bitmap.Save($stream,[Drawing.Imaging.ImageFormat]::Png)
    $images+=,@{Size=$size;Bytes=$stream.ToArray()}
    if($size -eq 256){$bitmap.Save((Join-Path $folder 'Bridge.png'),[Drawing.Imaging.ImageFormat]::Png)}
    $stream.Dispose();$brush.Dispose();$pen.Dispose();$g.Dispose();$bitmap.Dispose()
}
$file=[IO.File]::Create((Join-Path $folder 'Bridge.ico'));$writer=[IO.BinaryWriter]::new($file)
$writer.Write([uint16]0);$writer.Write([uint16]1);$writer.Write([uint16]$images.Count)
$offset=6+16*$images.Count
foreach($item in $images){$dimension=if($item.Size -eq 256){0}else{$item.Size};$writer.Write([byte]$dimension);$writer.Write([byte]$dimension);$writer.Write([byte]0);$writer.Write([byte]0);$writer.Write([uint16]1);$writer.Write([uint16]32);$writer.Write([uint32]$item.Bytes.Length);$writer.Write([uint32]$offset);$offset+=$item.Bytes.Length}
foreach($item in $images){$writer.Write([byte[]]$item.Bytes)}
$writer.Dispose();$file.Dispose()
