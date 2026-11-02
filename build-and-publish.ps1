#requires -Version 7.0
<#
.SYNOPSIS
    Automated one-click build and publish pipeline for Solforge and Solforge AI.
.DESCRIPTION
    Executes the 3-stage pipeline:
      1. build-runtime.ps1 (Solforge AI runtime)
      2. publish.ps1 (Solforge Editor & MCP Shim) -> Staging
      3. publish-ai.ps1 (AI Runtime & Electron Desktop) -> Staging
    Then automatically replaces files into sol\09-solforge\publish without requiring an empty folder.
#>
[CmdletBinding()]
param(
    [string]$SolRoot = "C:\Users\Seth\Desktop\Project - Sol\sol\09-solforge",
    [string]$FinalOutput = "C:\Users\Seth\Desktop\Project - Sol\sol\09-solforge\publish",
    [string]$NodeLicensePath = "C:\Users\Seth\Desktop\d2moddingsuite\node-license-v24.19.0\LICENSE",
    [string]$ElectronZip = "C:\Users\Seth\Desktop\d2moddingsuite\electron-v44.7.0\electron-v44.7.0-win32-x64.zip"
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# Resolves a file path from a list of preferred candidate paths, returning the first one that exists on disk.
function Resolve-FirstExisting([string[]]$Candidates) {
    foreach ($cand in $Candidates) {
        if ($cand -and (Test-Path -LiteralPath $cand -PathType Leaf)) {
            return (Resolve-Path -LiteralPath $cand).Path
        }
    }
    return $null
}

# Checks for running Solforge processes and terminates them to prevent file locking during publish.
function Close-RunningSolforgeProcesses([string[]]$ProcessNames) {
    foreach ($name in $ProcessNames) {
        $procs = Get-Process -Name $name -ErrorAction SilentlyContinue
        if ($procs) {
            Write-Host "Closing active process '$name' to avoid file locks..." -ForegroundColor Yellow
            $procs | Stop-Process -Force -ErrorAction SilentlyContinue
            Start-Sleep -Milliseconds 500
        }
    }
}

# Synchronizes contents from staging to final output directory, replacing existing files cleanly.
function Sync-DirectoryContents([string]$SourceDir, [string]$DestDir) {
    if (-not (Test-Path -LiteralPath $DestDir)) {
        New-Item -ItemType Directory -Path $DestDir -Force | Out-Null
    }
    # Use robocopy /MIR for fast, resilient mirroring with retries
    $robocopy = Get-Command robocopy.exe -ErrorAction SilentlyContinue
    if ($robocopy) {
        & robocopy.exe $SourceDir $DestDir /MIR /R:2 /W:1 /NP /NFL /NDL | Out-Null
        # In Robocopy, exit codes 0-7 indicate successful copy operations (8+ indicates fatal error)
        if ($LASTEXITCODE -ge 8) {
            throw "Robocopy synchronization failed with exit code $LASTEXITCODE."
        }
    } else {
        # Fallback to Copy-Item
        Copy-Item -Path (Join-Path $SourceDir "*") -Destination $DestDir -Recurse -Force
    }
}

# --- 1. Path Resolution & Pre-flight Validation ---
Write-Host "=====================================================================" -ForegroundColor Green
Write-Host " Solforge & Solforge AI Automated Build & Publish Pipeline" -ForegroundColor Green
Write-Host "=====================================================================" -ForegroundColor Green

$SolRoot = [IO.Path]::GetFullPath($SolRoot)
$FinalOutput = [IO.Path]::GetFullPath($FinalOutput)

# Resolve Node license
$resolvedLicense = Resolve-FirstExisting @(
    $NodeLicensePath,
    "C:\Users\Seth\Desktop\d2moddingsuite\node-license-v24.19.0\LICENSE",
    "C:\Users\Seth\Desktop\d2moddingsuite\archive\node-license-v24.19.0\LICENSE",
    (Join-Path $SolRoot "licenses\node\LICENSE")
)
if (-not $resolvedLicense) {
    throw "Node redistribution license not found at '$NodeLicensePath' or archive fallbacks."
}

# Resolve Electron zip
$resolvedElectron = Resolve-FirstExisting @(
    $ElectronZip,
    "C:\Users\Seth\Desktop\d2moddingsuite\electron-v44.7.0\electron-v44.7.0-win32-x64.zip",
    "C:\Users\Seth\Desktop\d2moddingsuite\archive\electron-v44.7.0\electron-v44.7.0-win32-x64.zip"
)
if (-not $resolvedElectron) {
    throw "Electron distribution zip not found at '$ElectronZip' or archive fallbacks."
}

Write-Host "Solforge Root:   $SolRoot" -ForegroundColor Gray
Write-Host "Final Output:    $FinalOutput" -ForegroundColor Gray
Write-Host "Node License:    $resolvedLicense" -ForegroundColor Gray
Write-Host "Electron Zip:    $resolvedElectron" -ForegroundColor Gray

# Close any running Solforge / AI processes to release file locks
Close-RunningSolforgeProcesses @('Solforge', 'solforge-mcp', 'solforge-ai')

# Setup clean staging directory (required by publish.ps1 and publish-ai.ps1)
$stagingDir = Join-Path $env:TEMP ("solforge-publish-staging-" + [Guid]::NewGuid().ToString("N"))
if (Test-Path -LiteralPath $stagingDir) {
    Remove-Item -LiteralPath $stagingDir -Recurse -Force -ErrorAction SilentlyContinue
}
New-Item -ItemType Directory -Path $stagingDir -Force | Out-Null

try {
    # --- Step 1: Build AI Runtime ---
    Write-Host "`n[Step 1/3] Building AI Runtime (build-runtime.ps1)..." -ForegroundColor Cyan
    $buildRuntimeScript = Join-Path $SolRoot "solforge-ai-build\build-runtime.ps1"
    if (-not (Test-Path -LiteralPath $buildRuntimeScript)) {
        $buildRuntimeScript = Join-Path $SolRoot "solforge-ai\build-runtime.ps1"
    }
    if (-not (Test-Path -LiteralPath $buildRuntimeScript)) {
        throw "build-runtime.ps1 not found in $SolRoot"
    }
    & pwsh -NoProfile -File $buildRuntimeScript
    if ($LASTEXITCODE -ne 0) { throw "build-runtime.ps1 failed with exit code $LASTEXITCODE." }

    # --- Step 2: Publish Editor & MCP Shim into Staging ---
    Write-Host "`n[Step 2/3] Publishing Solforge Editor & MCP Shim (publish.ps1)..." -ForegroundColor Cyan
    $publishScript = Join-Path $SolRoot "publish.ps1"
    if (-not (Test-Path -LiteralPath $publishScript)) {
        throw "publish.ps1 not found in $SolRoot"
    }
    & pwsh -NoProfile -File $publishScript -Output $stagingDir
    if ($LASTEXITCODE -ne 0) { throw "publish.ps1 failed with exit code $LASTEXITCODE." }

    # --- Step 3: Publish AI Runtime & Desktop into Staging ---
    Write-Host "`n[Step 3/3] Publishing Solforge AI Runtime & Desktop (publish-ai.ps1)..." -ForegroundColor Cyan
    $publishAiScript = Join-Path $SolRoot "solforge-ai-build\publish-ai.ps1"
    if (-not (Test-Path -LiteralPath $publishAiScript)) {
        $publishAiScript = Join-Path $SolRoot "solforge-ai\publish-ai.ps1"
    }
    if (-not (Test-Path -LiteralPath $publishAiScript)) {
        throw "publish-ai.ps1 not found in $SolRoot"
    }
    & pwsh -NoProfile -File $publishAiScript -Output $stagingDir -NodeLicensePath $resolvedLicense -ElectronZip $resolvedElectron
    if ($LASTEXITCODE -ne 0) { throw "publish-ai.ps1 failed with exit code $LASTEXITCODE." }

    # --- Step 4: Synchronize & Replace Files in Final Destination ---
    Write-Host "`n[Finalizing] Synchronizing files into $FinalOutput..." -ForegroundColor Cyan
    Sync-DirectoryContents -SourceDir $stagingDir -DestDir $FinalOutput

    Write-Host "`n=====================================================================" -ForegroundColor Green
    Write-Host " BUILD AND PUBLISH COMPLETE!" -ForegroundColor Green
    Write-Host " Output Folder: $FinalOutput" -ForegroundColor Green
    Write-Host "=====================================================================" -ForegroundColor Green
}
finally {
    # Clean up temporary staging
    if (Test-Path -LiteralPath $stagingDir) {
        Remove-Item -LiteralPath $stagingDir -Recurse -Force -ErrorAction SilentlyContinue
    }
}
