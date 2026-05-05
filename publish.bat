@echo off
chcp 65001 >nul
echo ========================================
echo   AutoUnpackTool Publish Script
echo ========================================
echo.

REM Check if dotnet is installed
where dotnet >nul 2>&1
if %errorlevel% neq 0 (
    echo [ERROR] dotnet command not found
    echo Please install .NET SDK: https://dotnet.microsoft.com/download
    pause
    exit /b 1
)

echo [INFO] Current .NET version:
dotnet --version
echo.

REM Clean old publish files
if exist publish (
    echo [INFO] Cleaning old publish files...
    rmdir /s /q publish
)

echo.
echo [INFO] Starting publish...
echo.

REM Publish as single-file application (self-contained, no .NET required for users)
echo [CONFIG] Publish settings:
echo    - Mode: Release
echo    - Platform: Windows x64
echo    - Self-contained: Yes (includes .NET runtime)
echo    - Single file: Yes
echo.

dotnet publish -c Release -r win-x64 ^
    --self-contained true ^
    -p:PublishSingleFile=true ^
    -p:IncludeNativeLibrariesForSelfExtract=true ^
    -p:DebugType=none ^
    -p:DebugSymbols=false ^
    -o ./publish

if %errorlevel% neq 0 (
    echo.
    echo [ERROR] Publish failed!
    pause
    exit /b 1
)

echo.
echo [SUCCESS] Publish completed!
echo.
echo [INFO] Published files location: publish directory
echo.
echo [INFO] File size:
dir publish\AutoUnpackTool.exe | findstr AutoUnpackTool
echo.
echo [TIPS]:
echo    1. Zip the publish folder and distribute to users
echo    2. Users need to configure 7z.exe path on first run
echo    3. Recommend including usage documentation
echo.
pause
