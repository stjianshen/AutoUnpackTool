# AutoUnpackTool 发布指南

## 🚀 快速发布

### 方式一：使用发布脚本（推荐）

1. 双击运行 `publish.bat`
2. 等待发布完成
3. 在 `publish` 目录找到 `AutoUnpackTool.exe`
4. 将整个 `publish` 文件夹打包为 zip 分发给用户

### 方式二：手动发布

```powershell
# 进入项目目录
cd e:\learn\AutoUnpackTool

# 发布为单文件应用（自包含，用户无需安装 .NET）
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o ./publish
```

---

## 📦 发布选项对比

### 选项 1：自包含单文件（推荐用于分发）

**优点：**
- ✅ 用户无需安装 .NET Runtime
- ✅ 单个 exe 文件，方便分发
- ✅ 即下即用

**缺点：**
- ❌ 文件较大（约 60-80 MB）

**命令：**
```powershell
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o ./publish
```

---

### 选项 2：依赖框架（体积小）

**优点：**
- ✅ 文件很小（约 5-10 MB）
- ✅ 更新 .NET Runtime 即可享受性能提升

**缺点：**
- ❌ 用户需要先安装 .NET 10.0 Runtime
- ❌ 部署步骤多一步

**命令：**
```powershell
dotnet publish -c Release -r win-x64 --self-contained false -o ./publish
```

**用户需要安装：**
- 下载链接：https://dotnet.microsoft.com/zh-cn/download/dotnet/10.0
- 选择 ".NET Desktop Runtime 10.0" for Windows x64

---

## 📋 分发清单

发布给用户时，建议包含以下内容：

```
AutoUnpackTool_v1.0.zip
├── AutoUnpackTool.exe          # 主程序
├── README.md                   # 使用说明
├── passwords.txt               # 示例密码本（可选）
└── CHANGELOG.md                # 版本更新说明（可选）
```

---

## ⚙️ 用户首次使用流程

1. **解压文件**到任意目录
2. **双击运行** `AutoUnpackTool.exe`
3. **配置 7z.exe 路径**：
   - 点击"软件设置"按钮
   - 浏览并选择 7z.exe（通常在 `C:\Program Files\7-Zip\7z.exe`）
   - 如果未安装 7-Zip，提示用户下载安装：https://www.7-zip.org/
4. **开始使用**：拖拽压缩文件到窗口

---

## 🔧 高级发布配置

### 添加版本信息

编辑 `AutoUnpackTool.csproj`，在 `<PropertyGroup>` 中添加：

```xml
<Version>1.0.0</Version>
<AssemblyVersion>1.0.0.0</AssemblyVersion>
<FileVersion>1.0.0.0</FileVersion>
<Company>Your Company</Company>
<Product>AutoUnpackTool</Product>
<Description>智能自动解压工具</Description>
```

### 创建安装包（可选）

使用 **Inno Setup** 创建专业安装程序：

1. 下载 Inno Setup：https://jrsoftware.org/isdl.php
2. 创建 `.iss` 脚本文件
3. 编译生成 `.exe` 安装程序

示例脚本：
```ini
[Setup]
AppName=AutoUnpackTool
AppVersion=1.0
DefaultDirName={pf}\AutoUnpackTool
OutputBaseFilename=AutoUnpackTool_Setup

[Files]
Source: "publish\*"; DestDir: "{app}"; Flags: recursesubdirs

[Icons]
Name: "{group}\AutoUnpackTool"; Filename: "{app}\AutoUnpackTool.exe"
```

---

## 📊 发布到 GitHub Releases

1. 访问：https://github.com/stjianshen/AutoUnpackTool/releases
2. 点击 "Create a new release"
3. 填写版本信息（如 v1.0.0）
4. 上传发布的 zip 文件
5. 发布

---

## 💡 最佳实践

1. **版本号管理**：每次发布递增版本号
2. **更新日志**：记录每个版本的变更
3. **测试**：发布前在不同电脑上测试
4. **文档**：提供清晰的使用说明
5. **7-Zip 依赖**：明确告知用户需要安装 7-Zip

---

## ❓ 常见问题

### Q: 发布后文件太大怎么办？
A: 使用 `--self-contained false` 选项，但用户需要安装 .NET Runtime。

### Q: 如何支持其他平台（如 Linux）？
A: 修改 `-r` 参数：
- Linux x64: `-r linux-x64`
- macOS x64: `-r osx-x64`
- macOS ARM: `-r osx-arm64`

### Q: 可以加密发布文件吗？
A: 可以使用第三方工具对 exe 进行加壳保护，但不推荐（可能被误报为病毒）。

---

**祝发布顺利！** 🎉
