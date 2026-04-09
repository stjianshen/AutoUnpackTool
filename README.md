# AutoUnpackTool - 自动解压工具

一个基于 C# WPF 开发的智能自动解压工具，支持自动测试密码并解压加密的压缩文件。

## ✨ 功能特性

### 已实现
- ✅ **文件拖拽**：支持拖拽单个或多个压缩文件到窗口
- ✅ **自动解压**：拖拽后自动开始解压（可关闭）
- ✅ **智能密码测试**：
  - **无密码优先**：先尝试无密码解压，提高效率
  - **LRU 智能排序**：基于使用频率和最近使用时间的综合评分算法 (`Score = UsageCount * 10 + 100 * exp(-hoursSinceLastUse / 24)`)，高频且最近使用的密码优先测试
  - **增量测试**：避免对已测试文件的重复遍历
- ✅ **实际解压功能**：集成 7-Zip 实现真实解压
- ✅ **软件设置**：可配置 7z.exe 路径、线程数、文件处理方式
- ✅ **密码本管理**：
  - **路径配置**：支持通过 `AppSettings.PasswordFilePath` 配置外部密码文件路径
  - **永久密码**：直接保存在 `settings.json` 中
  - **一次性密码**：运行时添加，重启后自动清除（内存存储）
- ✅ **异步处理**：使用 async/await 确保 UI 流畅
- ✅ **详细日志**：实时显示处理过程和状态
- ✅ **配置保存**：自动保存设置到 `%APPDATA%\AutoUnpackTool\settings.json`

### 规划中
- ⏳ 多层压缩递归解压
- ⏳ 分卷压缩支持
- ⏳ 移动到回收站功能
- ⏳ 更多文件处理选项

## 🚀 快速开始

### 系统要求
- Windows 10/11
- .NET 10.0 Runtime
- 7-Zip（必需）

### 安装步骤

1. **下载并安装 7-Zip**
   - 访问：https://www.7-zip.org/
   - 下载并安装 Windows 版本
   - 记住 `7z.exe` 的安装路径（通常在 `C:\Program Files\7-Zip\`）

2. **编译项目**
   ```bash
   cd AutoUnpackTool
   dotnet restore
   dotnet build
   ```

3. **运行程序**
   ```bash
   dotnet run
   ```

### 首次配置

1. 启动程序后，点击 **"软件设置"** 按钮
2. 点击 **"浏览..."** 选择 7z.exe 文件
3. 配置其他选项（可选）
4. 点击 **"保存"**

详细使用说明请查看 [USAGE.md](USAGE.md)

## 📖 使用流程

1. **配置 7z.exe 路径**（仅首次需要）
2. **选择输出目录**
3. **拖拽压缩文件到窗口**
4. **等待自动解压完成**

程序会自动：
- 尝试无密码解压
- 逐个测试密码本中的密码
- 找到正确密码后解压
- 显示详细的处理日志

## 📂 项目结构

```
AutoUnpackTool/
├── App.xaml                 # 应用程序入口
├── MainWindow.xaml          # 主窗口界面
├── MainWindow.xaml.cs       # 主窗口逻辑
├── SettingsDialog.xaml      # 设置对话框界面
├── SettingsDialog.xaml.cs   # 设置对话框逻辑
├── AppSettings.cs           # 配置管理类
├── SevenZipExtractor.cs     # 7z 解压引擎
├── passwords.txt            # 示例密码本
└── USAGE.md                 # 使用指南
```

## 🔧 技术栈

- **语言**：C# 13
- **框架**：.NET 10.0 WPF
- **UI**：XAML
- **解压引擎**：7-Zip CLI
- **配置存储**：JSON (System.Text.Json)
- **异步模型**：async/await + Task

## 📝 配置文件

配置保存在：
```
%APPDATA%\AutoUnpackTool\settings.json
```

包含以下设置：
- `SevenZipPath`: 7z.exe 路径
- `PasswordFilePath`: 密码本路径
- `ThreadCount`: 并发线程数
- `FileAfterExtract`: 解压后文件处理方式

## ⚠️ 注意事项

- **必须配置 7z.exe 路径才能使用解压功能**
- 建议在测试时使用不重要的文件
- 大文件解压可能需要较长时间
- 确保输出目录有足够的磁盘空间

##  贡献

欢迎提交 Issue 和 Pull Request！

## 📄 许可证

MIT License

---

**当前版本**：V1.0  
**最后更新**：2026-04-05