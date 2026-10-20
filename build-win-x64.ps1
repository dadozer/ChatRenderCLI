$ErrorActionPreference = "Stop"

try {
    Write-Host "============================================================================" -ForegroundColor Cyan
    Write-Host "Building ChatRenderCLI.exe & ChatRenderGUI.exe (Windows x64 Release)" -ForegroundColor Cyan
    Write-Host "============================================================================" -ForegroundColor Cyan

    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
        throw ".NET SDK was not found in PATH. Please install the .NET 10.0 SDK from https://dotnet.microsoft.com/download"
    }

    $ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
    $CliProjectPath = Join-Path $ScriptDir "TwitchDownloaderCLI\TwitchDownloaderCLI.csproj"
    $WpfProjectPath = Join-Path $ScriptDir "TwitchDownloaderWPF\TwitchDownloaderWPF.csproj"
    $OutputDir = Join-Path $ScriptDir "dist\win-x64"

    Write-Host "`n[1/2] Publishing ChatRenderCLI.exe..." -ForegroundColor Cyan
    dotnet publish $CliProjectPath `
        -c Release `
        -r win-x64 `
        --self-contained true `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:DebugType=none `
        -p:DebugSymbols=false `
        -o $OutputDir

    if ($LASTEXITCODE -ne 0) {
        throw "CLI build failed with exit code $LASTEXITCODE."
    }

    Write-Host "`n[2/2] Publishing ChatRenderGUI.exe (WPF)..." -ForegroundColor Cyan
    dotnet publish $WpfProjectPath `
        -c Release `
        -r win-x64 `
        --self-contained true `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:IncludeAllContentForSelfExtract=true `
        -p:DebugType=none `
        -p:DebugSymbols=false `
        -o $OutputDir

    if ($LASTEXITCODE -ne 0) {
        throw "GUI build failed with exit code $LASTEXITCODE."
    }

    Write-Host ""
    Write-Host "[SUCCESS] Built standalone executables:" -ForegroundColor Green
    Write-Host "  $(Join-Path $OutputDir 'ChatRenderCLI.exe')" -ForegroundColor Green
    Write-Host "  $(Join-Path $OutputDir 'ChatRenderGUI.exe')" -ForegroundColor Green
    Write-Host ""
    Read-Host "Press Enter to close this window"
}
catch {
    Write-Host ""
    Write-Host "[ERROR] $($_.Exception.Message)" -ForegroundColor Red
    Write-Host ""
    Read-Host "Build failed. Press Enter to close this window"
    exit 1
}
