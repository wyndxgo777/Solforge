# Starts the developer build. Users run publish\Sunrise Studio.exe.
param([string]$Game, [string]$Project, [string]$Tag, [switch]$Warp)
$ErrorActionPreference = 'Stop'
$studioDll = Join-Path $PSScriptRoot 'src/SunriseStudio.App/bin/Release/net9.0-windows/Solforge.dll'
if (-not (Test-Path -LiteralPath $studioDll)) { & (Join-Path $PSScriptRoot 'build.ps1') }
$studioArgs = @($studioDll)
if ($Game) { $studioArgs += @('--game', $Game) }
if ($Project) { $studioArgs += @('--project', $Project) }
if ($Tag) { $studioArgs += @('--tag', $Tag) }
if ($Warp) { $studioArgs += '--warp' }
& dotnet @studioArgs
if ($LASTEXITCODE -ne 0) { throw 'Studio exited with an error.' }
