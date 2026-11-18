# Regenerate the Windows icon from the web favicon. No external image tools needed.
# Supports the rounded rectangle and path geometry used by LMT's favicon.svg.
$ErrorActionPreference='Stop'
Add-Type -AssemblyName PresentationCore,WindowsBase
$root=Split-Path $PSScriptRoot -Parent
[xml]$svg=[IO.File]::ReadAllText((Join-Path $root 'src/web/public/favicon.svg'))
$culture=[Globalization.CultureInfo]::InvariantCulture
$viewBox=$svg.DocumentElement.GetAttribute('viewBox').Split(' ')
$width=[double]::Parse($viewBox[2],$culture)
$height=[double]::Parse($viewBox[3],$culture)
$rect=$svg.DocumentElement.SelectSingleNode("*[local-name()='rect']")
$path=$svg.DocumentElement.SelectSingleNode("*[local-name()='path']")
if(!$rect -or !$path){throw 'Unsupported favicon geometry. Update the icon generator for the new SVG.'}
$background=[Windows.Media.BrushConverter]::new().ConvertFromString($rect.GetAttribute('fill'))
$stroke=[Windows.Media.BrushConverter]::new().ConvertFromString($path.GetAttribute('stroke'))
$radius=[double]::Parse($rect.GetAttribute('rx'),$culture)
$pen=[Windows.Media.Pen]::new($stroke,[double]::Parse($path.GetAttribute('stroke-width'),$culture))
$pen.StartLineCap=[Windows.Media.PenLineCap]::Round
$pen.EndLineCap=[Windows.Media.PenLineCap]::Round
$geometry=[Windows.Media.Geometry]::Parse($path.GetAttribute('d'))
$sizes=@(16,20,24,32,40,48,64,96,128,256)
$images=[Collections.Generic.List[byte[]]]::new()
foreach($size in $sizes){
    $visual=[Windows.Media.DrawingVisual]::new()
    $drawing=$visual.RenderOpen()
    $drawing.PushTransform([Windows.Media.ScaleTransform]::new($size*4/$width,$size*4/$height))
    $drawing.DrawRoundedRectangle($background,$null,[Windows.Rect]::new(0,0,$width,$height),$radius,$radius)
    $drawing.DrawGeometry($null,$pen,$geometry)
    $drawing.Close()
    $bitmap=[Windows.Media.Imaging.RenderTargetBitmap]::new($size*4,$size*4,96,96,[Windows.Media.PixelFormats]::Pbgra32)
    $bitmap.Render($visual)
    $downsample=[Windows.Media.DrawingVisual]::new()
    [Windows.Media.RenderOptions]::SetBitmapScalingMode($downsample,[Windows.Media.BitmapScalingMode]::HighQuality)
    $context=$downsample.RenderOpen()
    $context.DrawImage($bitmap,[Windows.Rect]::new(0,0,$size,$size))
    $context.Close()
    $final=[Windows.Media.Imaging.RenderTargetBitmap]::new($size,$size,96,96,[Windows.Media.PixelFormats]::Pbgra32)
    $final.Render($downsample)
    $encoder=[Windows.Media.Imaging.PngBitmapEncoder]::new()
    $encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($final))
    $stream=[IO.MemoryStream]::new()
    try{$encoder.Save($stream);$images.Add($stream.ToArray())}finally{$stream.Dispose()}
}
$directory=Join-Path $root 'src/desktop/Assets'
[void][IO.Directory]::CreateDirectory($directory)
$output=Join-Path $directory 'Lmt.ico'
$file=[IO.File]::Create($output)
$writer=[IO.BinaryWriter]::new($file)
try{
    $writer.Write([uint16]0);$writer.Write([uint16]1);$writer.Write([uint16]$sizes.Count)
    $offset=6+16*$sizes.Count
    for($i=0;$i -lt $sizes.Count;$i++){
        $dimension=if($sizes[$i] -eq 256){0}else{$sizes[$i]}
        $writer.Write([byte]$dimension);$writer.Write([byte]$dimension)
        $writer.Write([byte]0);$writer.Write([byte]0)
        $writer.Write([uint16]1);$writer.Write([uint16]32)
        $writer.Write([uint32]$images[$i].Length);$writer.Write([uint32]$offset)
        $offset+=$images[$i].Length
    }
    foreach($bytes in $images){$writer.Write($bytes)}
}finally{$writer.Dispose()}
Write-Host "Updated $output ($($sizes -join ', ') px)"
$logo=@"
<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
    <DrawingImage x:Key="LmtLogo">
        <DrawingImage.Drawing>
            <DrawingGroup>
                <GeometryDrawing Brush="$($rect.GetAttribute('fill'))">
                    <GeometryDrawing.Geometry>
                        <RectangleGeometry Rect="0,0,$width,$height" RadiusX="$radius" RadiusY="$radius" />
                    </GeometryDrawing.Geometry>
                </GeometryDrawing>
                <GeometryDrawing Geometry="$($path.GetAttribute('d'))">
                    <GeometryDrawing.Pen>
                        <Pen Brush="$($path.GetAttribute('stroke'))" Thickness="$($path.GetAttribute('stroke-width'))" StartLineCap="Round" EndLineCap="Round" />
                    </GeometryDrawing.Pen>
                </GeometryDrawing>
            </DrawingGroup>
        </DrawingImage.Drawing>
    </DrawingImage>
</ResourceDictionary>
"@
$logoPath=Join-Path $directory 'LmtLogo.xaml'
[IO.File]::WriteAllText($logoPath,($logo -replace "`r?`n","`r`n")+"`r`n",[Text.UTF8Encoding]::new($false))
Write-Host "Updated $logoPath (WPF vector logo)"
