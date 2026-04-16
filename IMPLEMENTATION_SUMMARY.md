# 扩展名匹配功能实现总结

## 实现概述

本次更新为 AutoUnpackTool 添加了可配置的扩展名匹配功能，允许用户自定义哪些文件应该被检测为压缩文件。系统采用三级过滤机制：

1. **排除列表** - 直接跳过，不进行任何检测
2. **包含列表 + 魔术数验证** - 扩展名匹配后用文件头特征验证
3. **纯魔术数检测** - 扩展名不匹配时直接通过文件头检测

## 修改的文件

### 1. AppSettings.cs
**新增属性：**
- `ArchiveExtensions` (string): 需要检测的压缩文件扩展名列表（逗号分隔）
- `ExcludedExtensions` (string): 需要排除的文件扩展名列表（逗号分隔）

**新增方法：**
- `GetArchiveExtensions()`: 解析并返回需要检测的扩展名集合
- `GetExcludedExtensions()`: 解析并返回需要排除的扩展名集合

**默认值：**
```csharp
ArchiveExtensions = ".zip,.rar,.7z,.tar,.gz,.bz2,.xz,.zst,.zstd,.tgz,.tbz2,.tbz,.txz,.cab,.iso,.wim,.arj,.lzh,.cpio,.rpm,.deb"
ExcludedExtensions = ".exe,.dll,.msi,.doc,.docx,.xls,.xlsx,.ppt,.pptx,.pdf,.jpg,.jpeg,.png,.gif,.bmp,.mp3,.mp4,.avi,.mkv"
```

### 2. MainWindow.xaml.cs
**修改方法：**
- `IsArchiveFile(string filePath)`: 完全重写，实现三级过滤逻辑
- `IsArchiveFileByExtension(string filePath)`: 修改为使用设置中的扩展名列表

**新流程：**
```
1. 检查扩展名是否在排除列表中 → 是：直接返回 false
2. 检查扩展名是否在包含列表中 → 是：用魔术数验证
   - 验证通过：返回 true
   - 验证失败：返回 false
3. 扩展名不在列表中 → 直接用魔术数检测
   - 检测通过：返回 true
   - 检测失败：返回 false
```

**日志输出：**
- `[跳过] filename: 扩展名在排除列表中`
- `[检测] filename: 扩展名匹配，进行魔术数验证...`
- `[确认] filename: 魔术数验证通过，是压缩文件`
- `[拒绝] filename: 魔术数验证失败，不是压缩文件`
- `[检测] filename: 扩展名不匹配，尝试魔术数检测...`

### 3. SettingsDialog.xaml
**新增UI元素：**
- 第7行：压缩文件扩展名检测区域
  - `TxtArchiveExtensions`: 多行文本框，用于输入需要检测的扩展名
  - `TxtExcludedExtensions`: 多行文本框，用于输入需要排除的扩展名
  - 提示文本说明功能用法

**布局调整：**
- 原有 Grid.Row 7-9 的元素下移到 Row 8-10
- 窗口高度自适应（SizeToContent="Height"）

### 4. SettingsDialog.xaml.cs
**修改方法：**
- `LoadSettings()`: 添加加载扩展名设置的代码
- `BtnSave_Click()`: 添加保存扩展名设置的代码

## 技术细节

### 扩展名解析逻辑
```csharp
public HashSet<string> GetArchiveExtensions()
{
    var extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    if (!string.IsNullOrWhiteSpace(ArchiveExtensions))
    {
        foreach (var ext in ArchiveExtensions.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
        {
            string trimmed = ext.Trim().ToLowerInvariant();
            if (!trimmed.StartsWith("."))
                trimmed = "." + trimmed;  // 自动添加点号
            extensions.Add(trimmed);
        }
    }
    return extensions;
}
```

**特点：**
- 大小写不敏感（IgnoreCase）
- 自动处理带/不带点号的扩展名
- 去除空白字符
- 支持空值检查

### 优先级规则
1. **排除列表 > 包含列表**：即使扩展名同时在两个列表中，也会被排除
2. **魔术数验证 > 扩展名匹配**：扩展名匹配后必须通过魔术数验证才算有效
3. **分卷压缩包例外**：分卷格式不受扩展名列表限制

### 性能优化
- 排除列表中的文件直接跳过，不读取文件内容
- 使用 HashSet 实现 O(1) 时间复杂度的查找
- 详细的日志输出便于调试和问题排查

## 测试场景

### 场景 1：排除列表生效
```
输入：test.exe
预期：直接跳过，不读取文件
日志：[跳过] test.exe: 扩展名在排除列表中
```

### 场景 2：包含列表 + 魔术数验证成功
```
输入：archive.zip（真实ZIP文件）
预期：确认为压缩文件
日志：
  [检测] archive.zip: 扩展名匹配，进行魔术数验证...
  [确认] archive.zip: 魔术数验证通过，是压缩文件
```

### 场景 3：包含列表 + 魔术数验证失败
```
输入：fake.rar（实际是文本文件）
预期：拒绝作为压缩文件
日志：
  [检测] fake.rar: 扩展名匹配，进行魔术数验证...
  [拒绝] fake.rar: 魔术数验证失败，不是压缩文件
```

### 场景 4：未知扩展名 + 魔术数匹配
```
输入：unknown_file（实际是7z文件）
预期：通过魔术数检测确认为压缩文件
日志：
  [检测] unknown_file: 扩展名不匹配，尝试魔术数检测...
  [确认] unknown_file: 魔术数检测通过，是压缩文件
```

## 配置文件示例

settings.json 中的新字段：
```json
{
  "SevenZipPath": "...",
  "ArchiveExtensions": ".zip,.rar,.7z,.tar,.gz,.bz2,.xz,.zst,.zstd",
  "ExcludedExtensions": ".exe,.dll,.msi,.doc,.docx,.pdf,.jpg,.png,.mp4",
  "PermanentPasswords": [...]
}
```

## 向后兼容性

- ✅ 现有配置文件会自动添加新字段并使用默认值
- ✅ 旧版本配置文件可以正常加载
- ✅ 不影响现有功能（密码管理、解压模式等）

## 使用说明

### 基本配置
1. 打开"软件设置"对话框
2. 在"压缩文件扩展名检测"区域编辑两个文本框
3. 点击"💾 保存配置"

### 常见配置示例

**只检测主流格式：**
```
需要检测的扩展名：.zip,.rar,.7z
需要排除的扩展名：（留空或使用默认值）
```

**排除所有文档和媒体文件：**
```
需要检测的扩展名：（使用默认值）
需要排除的扩展名：.doc,.docx,.xls,.xlsx,.ppt,.pptx,.pdf,.jpg,.png,.gif,.mp3,.mp4,.avi,.mkv
```

**添加自定义扩展名：**
```
需要检测的扩展名：.zip,.rar,.7z,.custom,.mypack
需要排除的扩展名：（使用默认值）
```

## 注意事项

1. **排除列表优先级最高**：设计如此，防止误检测
2. **魔术数验证更可靠**：防止伪装成压缩文件的恶意文件
3. **分卷压缩包特殊处理**：自动检测，不受扩展名列表限制
4. **复合扩展名支持**：.tar.gz、.tar.zst 等会被正确识别
5. **日志详细**：每个文件的检测过程都有日志记录，便于排查问题

## 未来改进方向

1. 支持正则表达式匹配扩展名
2. 添加预设配置模板（如"仅主流格式"、"全部格式"等）
3. 支持导入/导出扩展名配置
4. 添加扩展名冲突检测警告
5. 统计每种扩展名的检测成功率

## 编译和运行

```bash
# 编译
cd e:\learn\AutoUnpackTool
dotnet build

# 运行
dotnet run
```

编译状态：✅ 成功（无警告，无错误）

## 相关文档

- [EXTENSION_MATCHING.md](./EXTENSION_MATCHING.md) - 详细的功能使用说明
- [DESIGN.md](./DESIGN.md) - 系统设计文档
- [README.md](./README.md) - 项目总览
