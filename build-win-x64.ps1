$ErrorActionPreference = "Stop"

$resourcesDir = Join-Path $PSScriptRoot "TwitchDownloaderCore\Resources"
$twemojiZip = Join-Path $resourcesDir "twemoji-17.0.3.zip"
$notoZip = Join-Path $resourcesDir "noto-emoji-2.051.zip"

if (!(Test-Path $twemojiZip)) {
    Write-Host "Downloading missing resource twemoji-17.0.3.zip..." -ForegroundColor Yellow
    Invoke-WebRequest -Uri "https://raw.githubusercontent.com/lay295/TwitchDownloader/master/TwitchDownloaderCore/Resources/twemoji-17.0.3.zip" -OutFile $twemojiZip
}

if (!(Test-Path $notoZip)) {
    Write-Host "Downloading missing resource noto-emoji-2.051.zip..." -ForegroundColor Yellow
    Invoke-WebRequest -Uri "https://raw.githubusercontent.com/lay295/TwitchDownloader/master/TwitchDownloaderCore/Resources/noto-emoji-2.051.zip" -OutFile $notoZip
}

Write-Host "Building and publishing ChatRenderCLI.exe (win-x64, self-contained single-file)..." -ForegroundColor Cyan

dotnet publish TwitchDownloaderCLI/TwitchDownloaderCLI.csproj `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -o ./publish

$exePath = Join-Path $PSScriptRoot "publish\ChatRenderCLI.exe"
if (Test-Path $exePath) {
    $fileInfo = Get-Item $exePath
    Write-Host "Success! ChatRenderCLI.exe created at: $($fileInfo.FullName) ($([Math]::Round($fileInfo.Length / 1MB, 2)) MB)" -ForegroundColor Green
} else {
    Write-Error "Build failed: ChatRenderCLI.exe was not found in ./publish"
    exit 1
}
