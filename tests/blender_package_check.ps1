# Solforge Phase 17 check: package-blender.ps1 applies the manifest's exclude rules, puts the files
# at the zip root, and gives byte-identical zips for the same input. Needs no Blender or game.
param([Parameter(Mandatory)][string]$Scratch)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
$repo = Split-Path -Parent $PSScriptRoot
$packer = Join-Path $repo 'package-blender.ps1'
$addon = Join-Path $Scratch 'addon'
foreach ($folder in 'sub', '__pycache__', 'build', '.git', 'sub/__pycache__') { New-Item -ItemType Directory -Force (Join-Path $addon $folder) | Out-Null }
Set-Content -LiteralPath (Join-Path $addon 'blender_manifest.toml') -Value "schema_version = `"1.0.0`"`nid = `"fixture_addon`"`nversion = `"1.2.3`""
foreach ($file in '__init__.py', 'sub/mod.py', 'LICENSE', '__pycache__/a.pyc', 'sub/__pycache__/b.pyc', 'build/out.txt', '.git/HEAD', '.hidden', 'old.zip') {
    Set-Content -LiteralPath (Join-Path $addon $file) -Value $file
}
$first = & $packer -Output (Join-Path $Scratch 'one') -Source $addon | Select-Object -Last 1
$second = & $packer -Output (Join-Path $Scratch 'two') -Source $addon | Select-Object -Last 1
if ((Split-Path -Leaf $first) -ne 'fixture_addon-1.2.3.zip') { throw "Unexpected add-on zip name: $first" }
$zip = [IO.Compression.ZipFile]::OpenRead($first)
try { $names = @($zip.Entries | ForEach-Object FullName) } finally { $zip.Dispose() }
$expected = @('blender_manifest.toml', 'LICENSE', '__init__.py', 'sub/mod.py')
if (($names -join '|') -ne ($expected -join '|')) { throw "Add-on zip entries are wrong: $($names -join ', ')" }
if ((Get-FileHash $first).Hash -ne (Get-FileHash $second).Hash) { throw 'The add-on zip is not reproducible.' }
# The real add-on must pack too and carry its manifest and license.
$real = & $packer -Output (Join-Path $Scratch 'repo') | Select-Object -Last 1
$zip = [IO.Compression.ZipFile]::OpenRead($real)
try { $names = @($zip.Entries | ForEach-Object FullName) } finally { $zip.Dispose() }
foreach ($required in 'blender_manifest.toml', '__init__.py', 'LICENSE') {
    if ($names -notcontains $required) { throw "The packed add-on misses $required." }
}
if ($names | Where-Object { $_ -match '__pycache__|^\.' }) { throw 'The packed add-on holds cache or dot files.' }
Write-Host "ok: Blender add-on packaging ($($names.Count) files in $(Split-Path -Leaf $real))"
