@echo off
chcp 65001 >nul
echo ========================================
echo   AutoUnpackTool 发布脚本
echo ========================================
echo.

REM 检查 dotnet 是否安装
where dotnet >nul 2>&1
if %errorlevel% neq 0 (
    echo ❌ 错误：未找到 dotnet 命令
    echo 请先安装 .NET SDK: https://dotnet.microsoft.com/download
    pause
    exit /b 1
)

echo 📌 当前 .NET 版本：
dotnet --version
echo.

REM 清理旧的发布文件
if exist publish (
    echo 🗑️  清理旧的发布文件...
    rmdir /s /q publish
)

echo.
echo 📦 开始发布...
echo.

REM 发布为单文件应用（自包含，用户无需安装 .NET）
echo 🔧 发布配置：
echo    - 模式：Release
echo    - 平台：Windows x64
echo    - 自包含：是（包含 .NET 运行时）
echo    - 单文件：是
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
    echo ❌ 发布失败！
    pause
    exit /b 1
)

echo.
echo ✅ 发布成功！
echo.
echo 📂 发布文件位于：publish 目录
echo.
echo 📊 文件大小：
dir publish\AutoUnpackTool.exe | findstr AutoUnpackTool
echo.
echo 💡 提示：
echo    1. 将 publish 文件夹打包为 zip 分发给用户
echo    2. 用户首次运行需要配置 7z.exe 路径
echo    3. 建议同时提供使用说明文档
echo.
pause
