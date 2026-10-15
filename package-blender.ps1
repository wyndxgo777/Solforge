# Solforge Phase 17: packs the in-repo Blender add-on (blender/) as a Blender extension zip next to a
# Studio build. Files sit at the zip root (blender_manifest.toml first), the manifest's
# paths_exclude_pattern rules are applied, and entries are sorted with a fixed timestamp so the same
# commit always gives the same zip. Writes <Output>/<id>-<version>.zip and returns its path.
# The add-on is GPL-3.0-or-later (blender/LICENSE); it ships as its own zip beside Solforge.
param([string]$Output = (Join-Path $PSScriptRoot 'publish'), [string]$Source = (Join-Path $PSScriptRoot 'blender'))
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
$Source = [IO.Path]::GetFullPath($Source)
$manifestPath = Join-Path $Source 'blender_manifest.toml'
if (-not (Test-Path -LiteralPath $manifestPath)) { throw "No blender_manifest.toml in $Source." }
$manifest = Get-Content -LiteralPath $manifestPath -Raw
$id = [regex]::Match($manifest, '(?m)^id\s*=\s*"([^"]+)"').Groups[1].Value
$version = [regex]::Match($manifest, '(?m)^version\s*=\s*"([^"]+)"').Groups[1].Value
if (-not $id -or -not $version) { throw 'blender_manifest.toml has no id or version.' }

# The manifest's exclude rules: __pycache__/ anywhere, and at the root .git/, dot files, build/ and *.zip.
function Test-Excluded([string]$Relative) {
    $parts = $Relative.Split('/')
    if ($parts -contains '__pycache__') { return $true }
    if ($parts[0].StartsWith('.')) { return $true }
    if ($parts.Length -gt 1 -and $parts[0] -eq 'build') { return $true }
    if ($parts.Length -eq 1 -and $Relative.EndsWith('.zip', [StringComparison]::OrdinalIgnoreCase)) { return $true }
    return $false
}

# [string[]]: pipeline strings arrive wrapped in PSObject, which StringComparer.Ordinal would compare by culture.
$files = [string[]]@(Get-ChildItem -LiteralPath $Source -Recurse -File | ForEach-Object {
    $_.FullName.Substring($Source.Length).TrimStart('\', '/').Replace('\', '/')
} | Where-Object { -not (Test-Excluded $_) })
# Ordinal order, manifest first, so the entry list never depends on the machine's culture.
[Array]::Sort($files, [StringComparer]::Ordinal)
$files = @('blender_manifest.toml') + @($files | Where-Object { $_ -ne 'blender_manifest.toml' })
New-Item -ItemType Directory -Force $Output | Out-Null
$zipPath = Join-Path ([IO.Path]::GetFullPath($Output)) "$id-$version.zip"
$temporary = "$zipPath.$([Guid]::NewGuid().ToString('N')).tmp"
$stamp = [DateTimeOffset]::new(2000, 1, 1, 0, 0, 0, [TimeSpan]::Zero)
try {
    $stream = [IO.File]::Open($temporary, [IO.FileMode]::CreateNew)
    try {
        $zip = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Create)
        try {
            foreach ($relative in $files) {
                $entry = $zip.CreateEntry($relative, [IO.Compression.CompressionLevel]::Optimal)
                $entry.LastWriteTime = $stamp
                $writer = $entry.Open()
                try { $bytes = [IO.File]::ReadAllBytes((Join-Path $Source $relative)); $writer.Write($bytes, 0, $bytes.Length) }
                finally { $writer.Dispose() }
            }
        }
        finally { $zip.Dispose() }
    }
    finally { $stream.Dispose() }
    Move-Item -Force -LiteralPath $temporary -Destination $zipPath
}
finally { if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary } }
Write-Host "Blender add-on packed to $zipPath"
return $zipPath
