$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$root = Split-Path $PSScriptRoot -Parent
$assets = Join-Path $root 'src\C30SuperLogin.Desktop\Assets'
$image = New-Object Drawing.Bitmap 1024,1024
$g = [Drawing.Graphics]::FromImage($image)
$g.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::AntiAlias
$g.ScaleTransform(2,2)
[xml]$svg = [IO.File]::ReadAllText((Join-Path $assets 'app.svg'))
foreach ($shape in $svg.svg.ChildNodes) {
    if ($shape.LocalName -notin @('path','rect')) { continue }
    $path = New-Object Drawing.Drawing2D.GraphicsPath
    if ($shape.LocalName -eq 'rect') {
        $x=[single]$shape.x; $y=[single]$shape.y; $w=[single]$shape.width; $h=[single]$shape.height; $r=[single]$shape.rx; $d=2*$r
        $path.AddArc($x,$y,$d,$d,180,90); $path.AddArc(($x+$w-$d),$y,$d,$d,270,90)
        $path.AddArc(($x+$w-$d),($y+$h-$d),$d,$d,0,90); $path.AddArc($x,($y+$h-$d),$d,$d,90,90); $path.CloseFigure()
    } else {
        $tokens=[regex]::Matches($shape.d,'[MLCZ]|-?[0-9]+(?:\.[0-9]+)?') | ForEach-Object Value
        $i=0; $x=0.0; $y=0.0
        function Number([string]$v) { return [single]::Parse($v,[Globalization.CultureInfo]::InvariantCulture) }
        while ($i -lt $tokens.Count) {
            $command=$tokens[$i]; $i++
            switch ($command) {
                'M' { $x=Number $tokens[$i]; $y=Number $tokens[$i+1]; $i+=2; $path.StartFigure() }
                'L' { $nx=Number $tokens[$i]; $ny=Number $tokens[$i+1]; $i+=2; $path.AddLine($x,$y,$nx,$ny); $x=$nx; $y=$ny }
                'C' { $x1=Number $tokens[$i]; $y1=Number $tokens[$i+1]; $x2=Number $tokens[$i+2]; $y2=Number $tokens[$i+3]; $nx=Number $tokens[$i+4]; $ny=Number $tokens[$i+5]; $i+=6; $path.AddBezier($x,$y,$x1,$y1,$x2,$y2,$nx,$ny); $x=$nx; $y=$ny }
                'Z' { $path.CloseFigure() }
                default { throw 'Unsupported SVG path token' }
            }
        }
    }
    $id=$shape.fill.Replace('url(#','').Replace(')','')
    $gradient=$svg.svg.defs.linearGradient | Where-Object id -eq $id
    if (!$gradient) { throw 'Missing SVG gradient' }
    $brush=New-Object Drawing.Drawing2D.LinearGradientBrush ([Drawing.PointF]::new([single]$gradient.x1,[single]$gradient.y1)),([Drawing.PointF]::new([single]$gradient.x2,[single]$gradient.y2)),([Drawing.ColorTranslator]::FromHtml($gradient.stop[0].GetAttribute('stop-color'))),([Drawing.ColorTranslator]::FromHtml($gradient.stop[1].GetAttribute('stop-color')))
    $brush.WrapMode=[Drawing.Drawing2D.WrapMode]::TileFlipXY
    $g.FillPath($brush,$path); $brush.Dispose(); $path.Dispose()
}
$g.Dispose()
$pngs=@(); $sizes=@(16,20,24,32,40,48,64,128,256)
foreach ($size in $sizes) {
    $small=New-Object Drawing.Bitmap $size,$size
    $draw=[Drawing.Graphics]::FromImage($small); $draw.InterpolationMode=[Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $draw.DrawImage($image,0,0,$size,$size); $draw.Dispose()
    $stream=New-Object IO.MemoryStream; $small.Save($stream,[Drawing.Imaging.ImageFormat]::Png)
    $pngs+=,@($stream.ToArray()); $stream.Dispose()
    if ($size -eq 256) { $small.Save((Join-Path $assets 'app.png'),[Drawing.Imaging.ImageFormat]::Png) }
    $small.Dispose()
}
$file=[IO.File]::Create((Join-Path $assets 'app.ico')); $writer=New-Object IO.BinaryWriter $file
try {
    $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$sizes.Count)
    $offset=6+16*$sizes.Count
    for ($i=0;$i -lt $sizes.Count;$i++) {
        $dimension=if ($sizes[$i] -eq 256) {0} else {$sizes[$i]}
        $writer.Write([byte]$dimension); $writer.Write([byte]$dimension); $writer.Write([byte]0); $writer.Write([byte]0)
        $writer.Write([uint16]1); $writer.Write([uint16]32); $writer.Write([uint32]$pngs[$i].Count); $writer.Write([uint32]$offset)
        $offset+=$pngs[$i].Count
    }
    foreach ($png in $pngs) { $writer.Write([byte[]]$png) }
} finally { $writer.Dispose() }
$image.Dispose()
$check=New-Object Drawing.Icon (Join-Path $assets 'app.ico'); $check.Dispose()
Write-Output ('ICON PASS: SVG-driven original C portal, PNG 256, ICO '+($sizes -join ','))
