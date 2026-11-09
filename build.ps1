#requires -Version 7.0
<#
.SYNOPSIS
    Build and optionally publish the Solforge editor and MCP server.
.DESCRIPTION
    Without -FinalOutput: publishes into a timestamped test folder under the repo root.
    With -FinalOutput: stages the build first, then syncs it into the destination directory.
    The Solforge AI desktop app and runtime are intentionally not part of this build.
.PARAMETER Output
    Explicit output folder for the build. Must be new or empty. Defaults to a
    timestamped folder next to the repo root when no -FinalOutput is given.
.PARAMETER FinalOutput
    Destination directory for a deployment build. Build is staged to a temp
    folder first, then synced in. Retired 'solforge-ai' and 'ai-runtime' dirs
    in the destination are archived to Google Drive before sync.
.PARAMETER PreflightOnly
    Validate environment only; do not build.
#>
[CmdletBinding()]
param(
    [string]$Output,
    [string]$FinalOutput,
    [switch]$PreflightOnly
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = $PSScriptRoot
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$logDir = Join-Path $root 'obj\build-logs'
$lock = Join-Path $root '.build.lock'
$log = Join-Path $logDir "build-$stamp.log"
$lockTaken = $false
$results = [System.Collections.Generic.List[object]]::new()

New-Item -ItemType Directory -Force -Path $logDir | Out-Null

function Say([string]$text) { Write-Host $text; Add-Content -LiteralPath $log -Value $text }
function Result([string]$name, [string]$state, [string]$detail = '') {
    $results.Add([pscustomobject]@{ Check = $name; State = $state; Detail = $detail })
    Write-Host ("[{0}] {1} {2}" -f $state, $name, $detail)
    Add-Content -LiteralPath $log -Value ("[{0}] {1} {2}" -f $state, $name, $detail)
}
function Stop-Build([string]$message) { throw $message }

# Copies staged files into a destination without deleting unrelated content.
function Sync-DirectoryContents([string]$SourceDir, [string]$DestDir) {
    if (-not (Test-Path -LiteralPath $DestDir)) {
        New-Item -ItemType Directory -Path $DestDir -Force | Out-Null
    }
    $robocopy = Get-Command robocopy.exe -ErrorAction SilentlyContinue
    if ($robocopy) {
        & robocopy.exe $SourceDir $DestDir /E /R:2 /W:1 /NP /NFL /NDL | Out-Null
        if ($LASTEXITCODE -ge 8) { Stop-Build "Robocopy failed with exit code $LASTEXITCODE." }
    } else {
        Copy-Item -Path (Join-Path $SourceDir '*') -Destination $DestDir -Recurse -Force
    }
}

# Archives retired AI output dirs to Google Drive before sync, then removes them.
function Archive-RetiredAiOutput([string]$OutputDir) {
    $names = @('solforge-ai', 'ai-runtime')
    $present = @($names | Where-Object { Test-Path -LiteralPath (Join-Path $OutputDir $_) -PathType Container })
    if ($present.Count -eq 0) { return }
    $drive = 'G:\My Drive'
    if (-not (Test-Path -LiteralPath $drive -PathType Container)) {
        Stop-Build 'Google Drive is unavailable; leaving the previous Solforge AI output untouched.'
    }
    $batchDir = Join-Path (Join-Path $drive ("Backups\sol-" + (Get-Date -Format 'yyyyMMdd'))) ("solforge-ai-retired-$stamp")
    if (Test-Path -LiteralPath $batchDir) { Stop-Build "Backup target already exists: $batchDir" }
    New-Item -ItemType Directory -Path $batchDir -Force | Out-Null
    $origins = [System.Collections.Generic.List[string]]::new()
    foreach ($name in $present) {
        $src = Join-Path $OutputDir $name
        $dst = Join-Path $batchDir $name
        Copy-Item -LiteralPath $src -Destination $dst -Recurse -Force
        $a = Get-ChildItem -LiteralPath $src -Recurse -File -Force | Measure-Object Length -Sum
        $b = Get-ChildItem -LiteralPath $dst -Recurse -File -Force | Measure-Object Length -Sum
        if ($a.Count -ne $b.Count -or $a.Sum -ne $b.Sum) {
            Stop-Build "Drive backup of $name failed count/size verification; original left in place."
        }
        $origins.Add("- ``$name`` from ``$src`` ($($a.Count) files, $($a.Sum) bytes).")
        Remove-Item -LiteralPath $src -Recurse -Force
    }
    $readme = @("# Retired Solforge AI build output", '', "Archived: $(Get-Date -Format 'yyyy-MM-dd').", '', 'Origins:') + $origins.ToArray()
    Set-Content -LiteralPath (Join-Path $batchDir 'README.md') -Value $readme -Encoding utf8
}

Write-Host "=====================================================================" -ForegroundColor Green
Write-Host " Solforge Build $stamp" -ForegroundColor Green
Write-Host "=====================================================================" -ForegroundColor Green

try {
    # --- Lock ---
    if (Test-Path -LiteralPath $lock) { Stop-Build "Another build may be running (lock: $lock)." }
    try {
        $lockStream = [IO.File]::Open($lock, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
        $lockTaken = $true
        $bytes = [Text.UTF8Encoding]::new($false).GetBytes("pid $PID started $(Get-Date -Format o)")
        try { $lockStream.Write($bytes, 0, $bytes.Length) } finally { $lockStream.Dispose() }
    } catch { Stop-Build "Could not take build lock $lock." }

    # --- Preflight ---
    if (Get-Process -Name destiny2 -ErrorAction SilentlyContinue) {
        Stop-Build 'destiny2.exe is running; close the game before building.'
    }
    foreach ($proc in Get-Process -Name Solforge, solforge-mcp -ErrorAction SilentlyContinue) {
        try {
            if ($proc.Path -and $Output -and $proc.Path.StartsWith($Output.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) {
                Stop-Build "Close $($proc.ProcessName) ($($proc.Id)) in the output folder before rebuilding."
            }
        } catch [System.ComponentModel.Win32Exception] { }
    }
    foreach ($proc in Get-Process -Name Solforge, 'solforge-mcp', 'solforge-ai' -ErrorAction SilentlyContinue) {
        if ($FinalOutput) {
            try {
                if ($proc.Path -and $proc.Path.StartsWith($FinalOutput.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) {
                    Stop-Build "Close $($proc.ProcessName) ($($proc.Id)) before publishing to $FinalOutput."
                }
            } catch [System.ComponentModel.Win32Exception] { }
        }
    }
    foreach ($tool in 'dotnet', 'git') {
        if (-not (Get-Command $tool -ErrorAction SilentlyContinue)) { Stop-Build "$tool is not on PATH." }
    }
    $publishScript = Join-Path $root 'publish.ps1'
    if (-not (Test-Path -LiteralPath $publishScript -PathType Leaf)) {
        Stop-Build "publish.ps1 not found: $publishScript"
    }
    Result 'preflight' 'PASS'

    if ($PreflightOnly) {
        Say 'Preflight-only mode; skipping build.'
        return
    }

    # --- Resolve output path ---
    $deployMode = [bool]$FinalOutput
    if ($deployMode) {
        # Staging dir; synced to FinalOutput after a successful build
        $buildTarget = Join-Path $env:TEMP ("solforge-staging-$stamp-" + [Guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path $buildTarget -Force | Out-Null
        Say "Deploy mode: staging -> $([IO.Path]::GetFullPath($FinalOutput))"
    } else {
        if (-not $Output) { $Output = Join-Path $root "publish-test-$stamp" }
        $buildTarget = [IO.Path]::GetFullPath($Output)
        if (Test-Path -LiteralPath $buildTarget) {
            if (-not (Test-Path -LiteralPath $buildTarget -PathType Container) -or
                (Get-ChildItem -LiteralPath $buildTarget -Force | Select-Object -First 1)) {
                Stop-Build "Output must be a new or empty folder: $buildTarget"
            }
        }
        Say "Build output: $buildTarget"
    }

    # --- Publish ---
    Say 'Publishing editor and MCP server (publish.ps1)...'
    & pwsh -NoProfile -File $publishScript -Output $buildTarget *>&1 | Tee-Object -FilePath $log -Append | Out-Host
    if ($LASTEXITCODE -ne 0) { Stop-Build "publish.ps1 failed with exit code $LASTEXITCODE." }

    # --- Validate outputs ---
    foreach ($file in 'Solforge.exe', 'solforge-mcp.exe') {
        if (-not (Test-Path -LiteralPath (Join-Path $buildTarget $file) -PathType Leaf)) {
            Stop-Build "Missing expected output: $file"
        }
        Result $file 'PASS'
    }
    $skillFiles = @(Get-ChildItem -LiteralPath (Join-Path $buildTarget 'skills') -Recurse -File -Filter 'SKILL.md' -ErrorAction SilentlyContinue)
    if ($skillFiles.Count -eq 0) { Stop-Build 'No project skill playbooks were packaged.' }
    Result 'project skills' 'PASS' "$($skillFiles.Count) playbooks"
    foreach ($folder in 'solforge-ai', 'ai-runtime') {
        if (Test-Path -LiteralPath (Join-Path $buildTarget $folder)) {
            Stop-Build "Unexpected retired component in output: $folder"
        }
    }
    Result 'no retired AI dirs' 'PASS'

    # --- Deploy (staging -> FinalOutput) ---
    if ($deployMode) {
        $dest = [IO.Path]::GetFullPath($FinalOutput)
        Say "Syncing build into $dest..."
        Archive-RetiredAiOutput $dest
        Sync-DirectoryContents -SourceDir $buildTarget -DestDir $dest
        Result 'deploy sync' 'PASS' $dest
        Remove-Item -LiteralPath $buildTarget -Recurse -Force -ErrorAction SilentlyContinue
        Write-Host "`n=====================================================================" -ForegroundColor Green
        Write-Host " BUILD & DEPLOY COMPLETE" -ForegroundColor Green
        Write-Host " Output: $dest" -ForegroundColor Green
        Write-Host "=====================================================================" -ForegroundColor Green
    } else {
        Result 'build' 'PASS' $buildTarget
        Write-Host "`n=====================================================================" -ForegroundColor Green
        Write-Host " BUILD COMPLETE" -ForegroundColor Green
        Write-Host " Output: $buildTarget" -ForegroundColor Green
        Write-Host "=====================================================================" -ForegroundColor Green
    }
} catch {
    Result 'build' 'FAIL' $_.Exception.Message
} finally {
    if ($lockTaken -and (Test-Path -LiteralPath $lock)) { Remove-Item -LiteralPath $lock -Force }
    $failed = @($results | Where-Object State -eq 'FAIL').Count
    Say ("{0} ({1} failed)  Log: {2}" -f $(if ($failed -eq 0) { 'PASS' } else { 'FAIL' }), $failed, $log)
    if ($failed -gt 0) { exit 1 }
}
