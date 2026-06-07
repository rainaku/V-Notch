# Build V-Notch Installer
# This script builds the Release version and creates the NSIS installer
#
# By default it produces a framework-dependent build (needs .NET 8 Desktop Runtime).
# Use -SelfContained to bundle the runtime so the app runs on a clean machine
# without installing .NET separately (larger installer).
param(
    [switch]$SelfContained,
    # Optional version override (e.g. injected from a git tag by CI).
    # When empty, the version baked into V-Notch.csproj is used.
    [string]$Version = ''
)

# Build a list of -p:Version arguments only when an override is supplied.
$versionArgs = @()
if ($Version) {
    $versionArgs = @(
        "-p:Version=$Version",
        "-p:AssemblyVersion=$Version",
        "-p:FileVersion=$Version",
        "-p:InformationalVersion=$Version"
    )
    Write-Host "Version override: $Version" -ForegroundColor Cyan
}

Write-Host "========================================" -ForegroundColor Cyan
Write-Host "V-Notch Installer Build Script" -ForegroundColor Cyan
if ($SelfContained) {
    Write-Host "Mode: Self-contained (.NET runtime bundled)" -ForegroundColor Cyan
} else {
    Write-Host "Mode: Framework-dependent (needs .NET 8 Runtime)" -ForegroundColor Cyan
}
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""

$publishDir = "release"

# Step 1: Clean previous publish
Write-Host "[1/4] Cleaning previous publish..." -ForegroundColor Yellow
if (Test-Path $publishDir) {
    Remove-Item -Path $publishDir -Recurse -Force
    Write-Host "      Cleaned $publishDir" -ForegroundColor Green
}

# Step 2: Build Release
Write-Host "[2/4] Building Release configuration..." -ForegroundColor Yellow
dotnet build .\V-Notch.csproj --configuration Release @versionArgs
if ($LASTEXITCODE -ne 0) {
    Write-Host "      Build failed!" -ForegroundColor Red
    exit 1
}
Write-Host "      Build successful" -ForegroundColor Green

# Step 3: Publish to release folder
Write-Host "[3/4] Publishing to $publishDir..." -ForegroundColor Yellow
if ($SelfContained) {
    # Self-contained: bundles the .NET runtime, runs without installing .NET 8.
    dotnet publish .\V-Notch.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none -p:DebugSymbols=false @versionArgs -o $publishDir
} else {
    # Framework-dependent single file - requires .NET 8 runtime.
    dotnet publish .\V-Notch.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none -p:DebugSymbols=false @versionArgs -o $publishDir
}
if ($LASTEXITCODE -ne 0) {
    Write-Host "      Publish failed!" -ForegroundColor Red
    exit 1
}

$exeVersion = (Get-Item "$publishDir\V-Notch.exe").VersionInfo.FileVersion
Write-Host "      Published successfully (v$exeVersion)" -ForegroundColor Green

# Step 4: Build NSIS installer
Write-Host "[4/4] Building NSIS installer..." -ForegroundColor Yellow

# Check if NSIS is installed
$nsisPath = "C:\Program Files (x86)\NSIS\makensis.exe"
if (-not (Test-Path $nsisPath)) {
    $nsisPath = "C:\Program Files\NSIS\makensis.exe"
}

if (-not (Test-Path $nsisPath)) {
    Write-Host "      NSIS not found! Please install NSIS from https://nsis.sourceforge.io/" -ForegroundColor Red
    Write-Host "      Release files are ready in '$publishDir'" -ForegroundColor Yellow
    exit 1
}

# Create installers directory if it doesn't exist
if (-not (Test-Path "installers")) {
    New-Item -ItemType Directory -Path "installers" | Out-Null
}

# Build installer
if ($SelfContained) {
    # Tell NSIS to skip the .NET runtime check/install (runtime is bundled).
    & $nsisPath "/DSELF_CONTAINED" "V-Notch-Setup.nsi"
} else {
    & $nsisPath "V-Notch-Setup.nsi"
}
if ($LASTEXITCODE -ne 0) {
    Write-Host "      NSIS build failed!" -ForegroundColor Red
    exit 1
}

Write-Host "      Installer created successfully" -ForegroundColor Green
Write-Host ""
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "Build Complete! (v$exeVersion)" -ForegroundColor Green
Write-Host "Installer: installers\V-Notch-Setup.exe" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
