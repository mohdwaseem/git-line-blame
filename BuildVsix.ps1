<#
.SYNOPSIS
    Builds the Git Line Blame VSIX extension for Visual Studio 2026.

.DESCRIPTION
    Builds GitLineBlame.csproj using VS 2026's MSBuild.
    Produces GitLineBlame.vsix in the bin\Release\net8.0-windows folder.

.REQUIREMENTS
    Visual Studio 2026 (Community, Professional, or Enterprise) must be installed.
    The "Visual Studio extension development" workload must be installed.
    To install it: open Visual Studio Installer → Modify → check
    "Visual Studio extension development" → Modify.

.USAGE
    From this directory (PowerShell):
        .\BuildVsix.ps1
    or Release build explicitly:
        .\BuildVsix.ps1 -Configuration Release

#>
param(
    [string]$Configuration = "Release"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

# ---- Locate VS 2026 MSBuild ----
$msbuild = $null
$vsInstallPaths = @(
    "C:\Program Files\Microsoft Visual Studio\18\Enterprise",
    "C:\Program Files\Microsoft Visual Studio\18\Professional",
    "C:\Program Files\Microsoft Visual Studio\18\Community",
    "C:\Program Files\Microsoft Visual Studio\18\Preview"
)
foreach ($vsPath in $vsInstallPaths) {
    $candidate = Join-Path $vsPath "MSBuild\Current\Bin\MSBuild.exe"
    if (Test-Path $candidate) {
        $msbuild = $candidate
        Write-Host "Found MSBuild: $msbuild"
        break
    }
}
if (-not $msbuild) {
    throw "Could not find VS 2026 MSBuild.exe. Is Visual Studio 2026 installed?"
}

# ---- Locate the VS 2026 VSToolsPath (set by extension dev workload) ----
$vsVersion  = "18.0"
$vsInstall  = Split-Path (Split-Path (Split-Path (Split-Path $msbuild)))
$vsToolsDir = Join-Path $vsInstall "MSBuild\Microsoft\VisualStudio\v$vsVersion"
$vsixTargets = Join-Path $vsToolsDir "VSSDK\Microsoft.VSSDK.targets"

if (-not (Test-Path $vsixTargets)) {
    Write-Warning ""
    Write-Warning "  '$vsixTargets' not found."
    Write-Warning "  The 'Visual Studio extension development' workload is not installed."
    Write-Warning "  Open Visual Studio Installer, click Modify on VS 2026, check"
    Write-Warning "  'Visual Studio extension development', then click Modify."
    Write-Warning ""
    Write-Warning "  Alternatively, open this project in VS 2026 and press Ctrl+Shift+B."
    Write-Warning "  VS handles VSIX packaging automatically inside the IDE."
    throw "Missing 'Visual Studio extension development' workload. See warning above."
}

# ---- Build ----
$project = Join-Path $PSScriptRoot "GitLineBlame.csproj"
Write-Host ""
Write-Host "Building $Configuration configuration..."
Write-Host ""

& $msbuild $project `
    /p:Configuration=$Configuration `
    /p:VSToolsPath=$vsToolsDir `
    /v:minimal `
    /nologo

if ($LASTEXITCODE -ne 0) {
    throw "Build failed (exit code $LASTEXITCODE)."
}

# ---- Find output VSIX ----
$vsix = Get-ChildItem (Join-Path $PSScriptRoot "bin\$Configuration") -Recurse -Filter "*.vsix" |
        Select-Object -First 1

if ($vsix) {
    Write-Host ""
    Write-Host "SUCCESS — VSIX built:"
    Write-Host "  $($vsix.FullName)"
    Write-Host "  Size: $([Math]::Round($vsix.Length / 1KB, 1)) KB"
    Write-Host ""
    Write-Host "To install: double-click the .vsix file, or run:"
    Write-Host "  & `"$vsInstall\Common7\IDE\VSIXInstaller.exe`" /quiet `"$($vsix.FullName)`""
} else {
    Write-Warning "Build succeeded but no .vsix file was found in bin\$Configuration."
}
