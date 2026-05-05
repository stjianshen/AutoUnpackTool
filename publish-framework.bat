@echo off
chcp 65001 >nul
echo ========================================
echo   AutoUnpackTool Publish (Framework-Dependent)
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
echo [INFO] Starting publish (Framework-Dependent)...
echo.

echo [CONFIG] Publish settings:
echo    - Mode: Release
echo    - Platform: Windows x64
echo    - Self-contained: No (requires .NET Runtime)
echo    - Single file: No
echo    - File size: ~5-10 MB (much smaller!)
echo.

dotnet publish -c Release -r win-x64 --self-contained false -o ./publish

if %errorlevel% neq 0 (
    echo.
    echo [ERROR] Publish failed!
    echo.
    echo [TIPS] If network issues persist, try:
    echo    1. Check your internet connection
    echo    2. Use a different network
    echo    3. Configure NuGet proxy if needed
    pause
    exit /b 1
)

echo.
echo [SUCCESS] Publish completed!
echo.
echo [INFO] Published files location: publish directory
echo.
echo [INFO] File sizes:
dir publish\*.exe | findstr AutoUnpackTool
echo.
echo [IMPORTANT] Users need to install .NET Desktop Runtime 10.0:
echo    Download: https://dotnet.microsoft.com/zh-cn/download/dotnet/10.0
echo    Select: .NET Desktop Runtime 10.0 for Windows x64
echo.
echo [TIPS]:
echo    1. Zip the publish folder and distribute to users
echo    2. Include runtime installation instructions
echo    3. Users need to configure 7z.exe path on first run
echo.
pause
