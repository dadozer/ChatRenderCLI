@echo off
setlocal

echo ============================================================================
echo Building ChatRenderCLI.exe ^& ChatRenderGUI.exe (Windows x64 Release)
echo ============================================================================

where dotnet >nul 2>nul
if %ERRORLEVEL% neq 0 (
    echo [ERROR] .NET SDK was not found in PATH.
    echo Please install the .NET 10.0 SDK from https://dotnet.microsoft.com/download
    pause
    exit /b 1
)

echo [1/2] Publishing ChatRenderCLI.exe...
dotnet publish "%~dp0TwitchDownloaderCLI\TwitchDownloaderCLI.csproj" ^
    -c Release ^
    -r win-x64 ^
    --self-contained true ^
    -p:PublishSingleFile=true ^
    -p:IncludeNativeLibrariesForSelfExtract=true ^
    -p:DebugType=none ^
    -p:DebugSymbols=false ^
    -o "%~dp0dist\win-x64"

if %ERRORLEVEL% neq 0 (
    echo.
    echo [ERROR] CLI build failed with exit code %ERRORLEVEL%.
    pause
    exit /b %ERRORLEVEL%
)

echo.
echo [2/2] Publishing ChatRenderGUI.exe (WPF)...
dotnet publish "%~dp0TwitchDownloaderWPF\TwitchDownloaderWPF.csproj" ^
    -c Release ^
    -r win-x64 ^
    --self-contained true ^
    -p:PublishSingleFile=true ^
    -p:IncludeNativeLibrariesForSelfExtract=true ^
    -p:IncludeAllContentForSelfExtract=true ^
    -p:DebugType=none ^
    -p:DebugSymbols=false ^
    -o "%~dp0dist\win-x64"

if %ERRORLEVEL% neq 0 (
    echo.
    echo [ERROR] GUI build failed with exit code %ERRORLEVEL%.
    pause
    exit /b %ERRORLEVEL%
)

echo.
echo [SUCCESS] Built standalone executables:
echo   %~dp0dist\win-x64\ChatRenderCLI.exe
echo   %~dp0dist\win-x64\ChatRenderGUI.exe
echo.
pause
