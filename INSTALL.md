# 自动解压工具 - 环境安装指南

## 📥 第一步：安装 .NET SDK

### 方法一：官方下载（推荐）

1. 访问 [.NET 下载页面](https://dotnet.microsoft.com/zh-cn/download)
2. 下载 **.NET 6.0 SDK** 或 **.NET 8.0 SDK**（推荐最新 LTS 版本）
3. 选择 **Windows** 版本
4. 运行安装程序，按提示完成安装
5. 安装完成后**重启命令行或 VS Code**

### 方法二：使用 winget（Windows 包管理器）

打开 PowerShell 或命令提示符，运行：

```powershell
winget install Microsoft.DotNet.SDK.6
```

或安装 .NET 8：

```powershell
winget install Microsoft.DotNet.SDK.8
```

### 验证安装

打开新的命令行窗口，运行：

```bash
dotnet --version
```

如果显示版本号（如 `6.0.418` 或 `8.0.100`），说明安装成功。

## 📥 第二步：安装 Visual Studio Code（可选但推荐）

1. 访问 [VS Code 官网](https://code.visualstudio.com/)
2. 下载并安装
3. 安装以下扩展：
   - **C# Dev Kit**（微软官方 C# 扩展）
   - **C#**（OmniSharp）

## 📥 第三步：运行项目

### 方式一：使用启动脚本（最简单）

双击项目根目录的 `run.bat` 文件。

### 方式二：手动命令行

```bash
# 进入项目目录
cd AutoUnpackTool

# 恢复依赖
dotnet restore

# 编译项目
dotnet build

# 运行程序
dotnet run
```

### 方式三：使用 Visual Studio 2022

1. 双击 `AutoUnpackTool.csproj` 文件
2. Visual Studio 会自动打开项目
3. 点击顶部的 "启动" 按钮（绿色三角形）

## 📦 第四步：集成 7-Zip 库（可选，用于实际解压功能）

当前原型版本使用模拟解压，如需实际解压功能：

### 1. 添加 NuGet 包

```bash
cd AutoUnpackTool
dotnet add package SevenZipSharp.Interop
```

### 2. 下载 7z.dll

- 访问 [7-Zip 官网](https://www.7-zip.org/)
- 下载 7-Zip 安装包
- 安装后从安装目录复制 `7z.dll` 到项目 `bin/Debug/net6.0-windows/` 目录

### 3. 或使用 Chocolatey（Windows 包管理器）

```powershell
# 安装 Chocolatey（如果还没有）
Set-ExecutionPolicy Bypass -Scope Process -Force; [System.Net.ServicePointManager]::SecurityProtocol = [System.Net.ServicePointManager]::SecurityProtocol -bor 3072; iex ((New-Object System.Net.WebClient).DownloadString('https://community.chocolatey.org/install.ps1'))

# 安装 7-Zip
choco install 7zip
```

## 🎯 第五步：开始使用

1. 运行程序
2. 将压缩文件拖拽到窗口
3. 选择输出目录
4. 点击"开始解压"
5. 查看日志输出

## ❓ 常见问题

### Q: dotnet 命令找不到
**A**: 请确保已安装 .NET SDK，并重启命令行窗口。

### Q: 编译时出现依赖错误
**A**: 运行 `dotnet restore` 恢复依赖。

### Q: 程序启动后界面显示异常
**A**: 确保使用 .NET 6.0 或更高版本，检查项目文件中的 `TargetFramework` 设置。

### Q: 如何修改密码本？
**A**: 编辑项目根目录的 `passwords.txt` 文件，每行一个密码。

## 📞 需要帮助？

如果遇到其他问题，请：
1. 检查 [.NET 官方文档](https://docs.microsoft.com/zh-cn/dotnet/)
2. 查看项目 `README.md` 文件
3. 提交 Issue 到项目仓库

---

祝使用愉快！🎉
