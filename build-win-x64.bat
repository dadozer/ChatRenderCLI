@echo off
setlocal

echo ============================================================================
echo Building ChatRenderCLI.exe (Windows x64 Self-Contained Single-File Release)
echo ============================================================================

where dotnet >nul 2>nul
if %ERRORLEVEL% neq 0 (
    echo [ERROR] .NET SDK was not found on your PATH.
    echo Please install the .NET 10.0 SDK from https://dotnet.microsoft.com/download/dotnet/10.0
    pause
    exit /b 1
)

if not exist "%~dp0TwitchDownloaderCore\Resources\twemoji-17.0.3.zip" (
    echo Restoring TwitchDownloaderCore\Resources\twemoji-17.0.3.zip...
    curl -fL "https://raw.githubusercontent.com/lay295/TwitchDownloader/master/TwitchDownloaderCore/Resources/twemoji-17.0.3.zip" -o "%~dp0TwitchDownloaderCore\Resources\twemoji-17.0.3.zip"
)

if not exist "%~dp0TwitchDownloaderCore\Resources\noto-emoji-2.051.zip" (
    echo Restoring TwitchDownloaderCore\Resources\noto-emoji-2.051.zip...
    curl -fL "https://raw.githubusercontent.com/lay295/TwitchDownloader/master/TwitchDownloaderCore/Resources/noto-emoji-2.051.zip" -o "%~dp0TwitchDownloaderCore\Resources\noto-emoji-2.051.zip"
)

dotnet publish "%~dp0TwitchDownloaderCLI\TwitchDownloaderCLI.csproj" ^
    -c Release ^
    -r win-x64 ^
    --self-contained true ^
    -p:PublishSingleFile=true ^
    -p:IncludeNativeLibrariesForSelfExtract=true ^
    -p:DebugType=Embedded ^
    -o "%~dp0publish"

if %ERRORLEVEL% neq 0 (
    echo.
    echo [ERROR] Build failed with exit code %ERRORLEVEL%.
    pause
    exit /b %ERRORLEVEL%
)

if exist "%~dp0publish\ChatRenderCLI.exe" (
    echo.
    echo ============================================================================
    echo [SUCCESS] Standalone executable created:
    echo   %~dp0publish\ChatRenderCLI.exe
    echo ============================================================================
    echo.
    echo Example usage:
    echo   "%~dp0publish\ChatRenderCLI.exe" chatrender -i chat.json -o chat.mp4
    echo   "%~dp0publish\ChatRenderCLI.exe" -i chat.json -o chat.mp4
    echo   "%~dp0publish\ChatRenderCLI.exe" ffmpeg -d
    echo   "%~dp0publish\ChatRenderCLI.exe" cache -c
) else (
    echo [ERROR] Build finished, but publish\ChatRenderCLI.exe was not found.
    pause
    exit /b 1
)

endlocal
