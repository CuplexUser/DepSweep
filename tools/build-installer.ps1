<#
.SYNOPSIS
    Publishes depsweep.exe + DepSweepGui.exe and packages them into an MSI.

.DESCRIPTION
    The version comes from git tags via MinVer (tag "v0.2.0" -> 0.2.0; no tag -> 0.1.0).
    Output: artifacts\DepSweep-<version>-win-x64.msi

.PARAMETER SelfContained
    Bundle the .NET runtime into the executables (bigger, but no .NET 10 runtime needed on the target machine).

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools\build-installer.ps1
    powershell -ExecutionPolicy Bypass -File tools\build-installer.ps1 -SelfContained
#>
param(
    [switch]$SelfContained,
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$artifacts = Join-Path $root "artifacts"
$publishDir = Join-Path $artifacts "publish"

function Invoke-Checked([string]$what, [scriptblock]$block) {
    Write-Host "==> $what" -ForegroundColor Cyan
    & $block
    if ($LASTEXITCODE -ne 0) { throw "$what failed (exit code $LASTEXITCODE)" }
}

# 1. Version from git tags (MinVer), as plain major.minor.patch.
$json = dotnet msbuild (Join-Path $root "src\DepSweep.Cli\DepSweep.Cli.csproj") -restore -t:MinVer `
    -getProperty:MinVerMajor -getProperty:MinVerMinor -getProperty:MinVerPatch | Out-String
if ($LASTEXITCODE -ne 0) { throw "Could not determine the version:`n$json" }
$p = ($json | ConvertFrom-Json).Properties
$version = "$($p.MinVerMajor).$($p.MinVerMinor).$($p.MinVerPatch)"
Write-Host "Version: $version" -ForegroundColor Green

# 2. Publish both executables side by side (single-file, win-x64).
if (Test-Path $publishDir) { Remove-Item -Recurse -Force $publishDir }
$common = @("-c", $Configuration, "-r", "win-x64", "-o", $publishDir, "-p:PublishSingleFile=true",
            "-p:DebugType=none", "--self-contained", ($SelfContained.IsPresent.ToString().ToLowerInvariant()))
if ($SelfContained) { $common += "-p:IncludeNativeLibrariesForSelfExtract=true" }

Invoke-Checked "Publish CLI" { dotnet publish (Join-Path $root "src\DepSweep.Cli\DepSweep.Cli.csproj") @common }
Invoke-Checked "Publish GUI" { dotnet publish (Join-Path $root "src\DepSweep.App\DepSweep.App.csproj") @common }

# 3. Build the MSI.
$msiOut = Join-Path $artifacts "msi"
Invoke-Checked "Build MSI" {
    dotnet build (Join-Path $root "installer\DepSweep.Installer.wixproj") -c Release -o $msiOut `
        "-p:DepSweepVersion=$version" "-p:AppPublishDir=$publishDir\"
}

$suffix = if ($SelfContained) { "-self-contained" } else { "" }
$final = Join-Path $artifacts "DepSweep-$version-win-x64$suffix.msi"
Copy-Item (Join-Path $msiOut "DepSweep.msi") $final -Force
Write-Host ""
Write-Host "Installer: $final" -ForegroundColor Green
