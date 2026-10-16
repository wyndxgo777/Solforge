<#
  Reads on-screen text with Windows.Media.Ocr (built into Windows 10/11; no download, no GPU).
  Windows PowerShell 5.1 projects WinRT types itself, so this needs no SDK, NuGet or pip package.

  One shot:  powershell -NoProfile -ExecutionPolicy Bypass -File ScreenOcr.ps1 -Path frame.png [-Region x,y,w,h] [-Scale 2]
  Server:    powershell -NoProfile -ExecutionPolicy Bypass -File ScreenOcr.ps1 -Serve
             then one JSON request per stdin line: {"path":"C:\\f.png","region":[x,y,w,h],"scale":2,"lang":"en-US"}
             and one JSON reply per stdout line.

  Reply: {"ok":true,"width":W,"height":H,"lang":"en-US","angle":0,"text":"...",
          "lines":[{"text":"...","box":[x,y,w,h],"words":[{"text":"...","box":[x,y,w,h]}]}]}
  Boxes are pixels in the ORIGINAL image (region offset and scale already undone). Errors: {"ok":false,"error":"..."}.
#>
param([string]$Path, [string]$Region, [double]$Scale = 0, [string]$Lang, [switch]$Serve)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Runtime.WindowsRuntime
$null = [Windows.Storage.StorageFile, Windows.Storage, ContentType = WindowsRuntime]
$null = [Windows.Storage.FileAccessMode, Windows.Storage, ContentType = WindowsRuntime]
$null = [Windows.Storage.Streams.IRandomAccessStream, Windows.Storage.Streams, ContentType = WindowsRuntime]
$null = [Windows.Graphics.Imaging.BitmapDecoder, Windows.Graphics.Imaging, ContentType = WindowsRuntime]
$null = [Windows.Graphics.Imaging.SoftwareBitmap, Windows.Graphics.Imaging, ContentType = WindowsRuntime]
$null = [Windows.Media.Ocr.OcrEngine, Windows.Foundation, ContentType = WindowsRuntime]
$null = [Windows.Media.Ocr.OcrResult, Windows.Foundation, ContentType = WindowsRuntime]
$null = [Windows.Globalization.Language, Windows.Globalization, ContentType = WindowsRuntime]

$asTask = [System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object {
    $_.Name -eq 'AsTask' -and $_.GetParameters().Count -eq 1 -and $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1'
} | Select-Object -First 1
function Await($operation, [Type]$type) {
    $task = $asTask.MakeGenericMethod($type).Invoke($null, @($operation))
    $null = $task.Wait(-1)
    $task.Result
}

$engines = @{}
function Get-Engine([string]$lang) {
    $key = if ($lang) { $lang } else { '' }
    if (-not $engines.ContainsKey($key)) {
        $engine = if ($lang) { [Windows.Media.Ocr.OcrEngine]::TryCreateFromLanguage([Windows.Globalization.Language]::new($lang)) }
                  else { [Windows.Media.Ocr.OcrEngine]::TryCreateFromUserProfileLanguages() }
        if ($null -eq $engine) { $engine = [Windows.Media.Ocr.OcrEngine]::TryCreateFromLanguage([Windows.Globalization.Language]::new('en-US')) }
        if ($null -eq $engine) { throw "No Windows OCR language is installed (Settings > Time & language > Language: add English)." }
        $engines[$key] = $engine
    }
    $engines[$key]
}

function Round1([double]$v) { [Math]::Round($v, 1) }

function Read-ScreenText([string]$path, $region, [double]$scale, [string]$lang) {
    $full = [System.IO.Path]::GetFullPath($path)
    $file = Await ([Windows.Storage.StorageFile]::GetFileFromPathAsync($full)) ([Windows.Storage.StorageFile])
    $stream = Await ($file.OpenAsync([Windows.Storage.FileAccessMode]::Read)) ([Windows.Storage.Streams.IRandomAccessStream])
    try {
        $decoder = Await ([Windows.Graphics.Imaging.BitmapDecoder]::CreateAsync($stream)) ([Windows.Graphics.Imaging.BitmapDecoder])
        $width = [int]$decoder.PixelWidth; $height = [int]$decoder.PixelHeight
        # Crop to the region (clamped to the image), then scale small text up: Windows OCR wants letters ~20+ px tall.
        # Auto is 2x up to 1280 px: on game HUD crops 3x sometimes loses whole lines that 2x reads.
        $rx = 0; $ry = 0; $rw = $width; $rh = $height
        if ($region -and $region.Count -eq 4) {
            $rx = [Math]::Max(0, [Math]::Min($width - 1, [int]$region[0])); $ry = [Math]::Max(0, [Math]::Min($height - 1, [int]$region[1]))
            $rw = [Math]::Max(1, [Math]::Min($width - $rx, [int]$region[2])); $rh = [Math]::Max(1, [Math]::Min($height - $ry, [int]$region[3]))
        }
        $max = [double][Windows.Media.Ocr.OcrEngine]::MaxImageDimension
        if ($scale -le 0) { $scale = if ([Math]::Max($rw, $rh) -le 1280) { 2.0 } else { 1.0 } }
        $scale = [Math]::Min($scale, [Math]::Min($max / $rw, $max / $rh))
        $transform = [Windows.Graphics.Imaging.BitmapTransform]::new()
        $transform.ScaledWidth = [uint32][Math]::Max(1, [Math]::Round($width * $scale))
        $transform.ScaledHeight = [uint32][Math]::Max(1, [Math]::Round($height * $scale))
        $transform.InterpolationMode = [Windows.Graphics.Imaging.BitmapInterpolationMode]::Fant
        $bounds = [Windows.Graphics.Imaging.BitmapBounds]::new()
        $bounds.X = [uint32][Math]::Floor($rx * $scale); $bounds.Y = [uint32][Math]::Floor($ry * $scale)
        $bounds.Width = [uint32][Math]::Max(1, [Math]::Min($transform.ScaledWidth - $bounds.X, [Math]::Round($rw * $scale)))
        $bounds.Height = [uint32][Math]::Max(1, [Math]::Min($transform.ScaledHeight - $bounds.Y, [Math]::Round($rh * $scale)))
        $transform.Bounds = $bounds
        $bitmap = Await ($decoder.GetSoftwareBitmapAsync([Windows.Graphics.Imaging.BitmapPixelFormat]::Bgra8,
            [Windows.Graphics.Imaging.BitmapAlphaMode]::Premultiplied, $transform,
            [Windows.Graphics.Imaging.ExifOrientationMode]::IgnoreExifOrientation,
            [Windows.Graphics.Imaging.ColorManagementMode]::DoNotColorManage)) ([Windows.Graphics.Imaging.SoftwareBitmap])
    } finally { $stream.Dispose() }

    $engine = Get-Engine $lang
    $result = Await ($engine.RecognizeAsync($bitmap)) ([Windows.Media.Ocr.OcrResult])
    $bitmap.Dispose()
    function Box($rect) {
        @((Round1 (($rect.X + $bounds.X) / $scale)), (Round1 (($rect.Y + $bounds.Y) / $scale)),
          (Round1 ($rect.Width / $scale)), (Round1 ($rect.Height / $scale)))
    }
    $lines = @(foreach ($line in $result.Lines) {
        $words = @(foreach ($word in $line.Words) { [ordered]@{ text = $word.Text; box = Box $word.BoundingRect } })
        $x0 = ($words | ForEach-Object { $_.box[0] } | Measure-Object -Minimum).Minimum
        $y0 = ($words | ForEach-Object { $_.box[1] } | Measure-Object -Minimum).Minimum
        $x1 = ($words | ForEach-Object { $_.box[0] + $_.box[2] } | Measure-Object -Maximum).Maximum
        $y1 = ($words | ForEach-Object { $_.box[1] + $_.box[3] } | Measure-Object -Maximum).Maximum
        [ordered]@{ text = $line.Text; box = @((Round1 $x0), (Round1 $y0), (Round1 ($x1 - $x0)), (Round1 ($y1 - $y0))); words = $words }
    })
    [ordered]@{
        ok = $true; width = $width; height = $height; region = @($rx, $ry, $rw, $rh); scale = (Round1 $scale)
        lang = $engine.RecognizerLanguage.LanguageTag; angle = (Round1 ([double]$result.TextAngle)); text = $result.Text; lines = $lines
    }
}

function Reply($object) { [Console]::Out.WriteLine(($object | ConvertTo-Json -Depth 8 -Compress)); [Console]::Out.Flush() }

[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
if ($Serve) {
    [Console]::InputEncoding = [System.Text.Encoding]::UTF8
    while ($null -ne ($request = [Console]::In.ReadLine())) {
        if (-not $request.Trim()) { continue }
        try {
            $r = $request | ConvertFrom-Json
            $s = if ($r.scale) { [double]$r.scale } else { 0 }
            Reply (Read-ScreenText $r.path $r.region $s $r.lang)
        } catch { Reply ([ordered]@{ ok = $false; error = $_.Exception.Message }) }
    }
} else {
    try {
        $box = if ($Region) { @($Region -split '[,\s]+' | Where-Object { $_ } | ForEach-Object { [int]$_ }) } else { $null }
        Reply (Read-ScreenText $Path $box $Scale $Lang)
    } catch { Reply ([ordered]@{ ok = $false; error = $_.Exception.Message }); exit 1 }
}
