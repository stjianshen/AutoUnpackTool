# AutoUnpackTool 综合设计文档

## 📋 目录

1. [项目概述](#1-项目概述)
2. [整体架构](#2-整体架构)
3. [核心机制](#3-核心机制)
4. [数据流转](#4-数据流转)
5. [UI设计与状态管理](#5-ui设计与状态管理)
6. [密码管理系统](#6-密码管理系统)
7. [关键算法与实现细节](#7-关键算法与实现细节)
8. [配置管理](#8-配置管理)
9. [历史问题与解决方案](#9-历史问题与解决方案)
10. [开发规范与最佳实践](#10-开发规范与最佳实践)

---

## 1. 项目概述

### 1.1 系统目标

AutoUnpackTool 是一个基于 C# WPF 开发的自动解压工具，旨在解决以下核心问题：

- **自动化密码测试**：根据密码本自动测试压缩文件密码，避免手动输入
- **递归解压支持**：自动识别并处理多层嵌套的压缩包
- **异步并发处理**：使用多线程技术提升大文件解压效率，保持 UI 响应流畅
- **智能后处理**：支持可配置的文件处理方式（删除、移动、保留）

### 1.2 核心功能

#### 已实现功能

✅ **文件拖拽与检测**
- 支持拖拽单个或多个文件到窗口
- 自动检测文件是否存在
- 基于扩展名和文件头识别压缩文件

✅ **分卷压缩支持**
- 自动检测多种分卷格式（.7z.001, .part1.rar, .001, .z01, .r00 等）
- 智能合并分卷信息，只添加主卷到处理队列
- 递归扫描文件夹时自动识别并合并分卷
- **可在设置中启用/禁用（默认开启）**

✅ **双队列异步架构**
- 密码测试队列与解压队列分离
- 信号量唤醒机制确保线程不会过早退出
- 多线程并发解压（可配置线程数）

✅ **树形结构展示**
- TreeView 展示文件层级关系
- 父子节点状态同步（反向传播机制）
- 实时进度显示

✅ **密码管理**
- 从配置文件加载永久密码
- 支持一次性临时密码
- 密码使用次数统计与自动排序（MRU）
- 密码本导入/导出功能

✅ **递归解压**
- 自动扫描解压后的子压缩包
- 统一树状处理流程（子项复用父项逻辑）
- 全量节点收集与多阶段处理

✅ **状态机管理**
- 5 种工作状态：Idle, Testing, Extracting, Completed, Failed
- 智能按钮切换（开始/停止）
- 完整的状态转换流程

#### 规划中功能

⏳ 更多压缩格式支持（RAR5, TAR.GZ 等）  
⏳ 解压后文件智能分类  

### 1.3 技术栈

| 类别 | 技术选型 |
|------|---------|
| **语言** | C# 12.0 |
| **框架** | .NET 10.0 (net10.0-windows) |
| **UI 框架** | WPF (Windows Presentation Foundation) |
| **解压引擎** | SevenZipSharp.Interop + 7z.dll |
| **并发模型** | async/await + Task + ConcurrentQueue |
| **数据持久化** | JSON 序列化（System.Text.Json） |
| **线程同步** | ManualResetEventSlim, TaskCompletionSource, lock |

### 1.4 运行环境要求

- **操作系统**：Windows 10/11
- **.NET Runtime**：.NET 10.0 或更高版本
- **必需文件**：7z.dll（与可执行文件同目录）

---

## 2. 整体架构

### 2.1 架构概览

```
┌─────────────────────────────────────────────────────┐
│                   UI Layer (WPF)                     │
│  MainWindow.xaml / SettingsDialog / PasswordManager  │
└──────────────────┬──────────────────────────────────┘
                   │
┌──────────────────▼──────────────────────────────────┐
│              Business Logic Layer                    │
│                                                      │
│  ┌──────────────┐    ┌──────────────────────────┐   │
│  │ State Machine│    │  Dual Queue Processor     │   │
│  │ (WorkState)  │    │                          │   │
│  └──────────────┘    │  ┌────────────────────┐  │   │
│                      │  │ Pending Queue       │  │   │
│                      │  │ (密码测试)          │  │   │
│                      │  └────────┬───────────┘  │   │
│                      │           │               │   │
│                      │  ┌────────▼───────────┐  │   │
│                      │  │ Extract Queue      │  │   │
│                      │  │ (解压操作)          │  │   │
│                      │  └────────────────────┘  │   │
│                      └──────────────────────────┘   │
└──────────────────┬──────────────────────────────────┘
                   │
┌──────────────────▼──────────────────────────────────┐
│              Data Access Layer                       │
│                                                      │
│  ┌──────────────┐    ┌──────────────────────────┐   │
│  │ FileItem Tree│    │  PasswordMap             │   │
│  │ (树形结构)    │    │  (线程安全字典)          │   │
│  └──────────────┘    └──────────────────────────┘   │
│                                                      │
│  ┌──────────────┐    ┌──────────────────────────┐   │
│  │ AppSettings  │    │  SevenZipExtractor       │   │
│  │ (JSON 配置)  │    │  (7-Zip 封装)            │   │
│  └──────────────┘    └──────────────────────────┘   │
└─────────────────────────────────────────────────────┘
```

### 2.2 核心数据结构

#### FileItem - 文件项（树形节点）

```csharp
public class FileItem : INotifyPropertyChanged
{
    // 基本信息
    public string FileName { get; set; }
    public string FilePath { get; set; }
    public long FileSize { get; set; }
    public string Status { get; set; } = "等待处理";
    public string? FoundPassword { get; set; }
    
    // 树形结构
    public ObservableCollection<FileItem> Children { get; set; } = new();
    public FileItem? Parent { get; set; }
    public bool IsParent => Children.Count > 0;
    
    // 分卷信息
    public ArchiveVolumeInfo? VolumeInfo { get; set; }
    
    // 反向传播机制字段
    private int _expectedChildrenCount = 0;
    private int _completedChildrenCount = 0;
    private bool _isMarkedComplete = false;
    
    // 线程锁
    private readonly object _lockObject = new();
    private bool _isLocked = false;
    
    // 方法
    public void SetExpectedChildrenCount(int count);
    public bool MarkChildComplete();
    public string GetProgressInfo();
    public bool TryLock();
    public void ReleaseLock();
    
    // INotifyPropertyChanged 实现
    public event PropertyChangedEventHandler? PropertyChanged;
    protected virtual void OnPropertyChanged(string propertyName);
}
```

**关键字段说明：**

| 字段 | 用途 |
|------|------|
| `Children` | 存储子压缩包列表，形成树形结构 |
| `Parent` | 指向父节点，用于反向传播 |
| `_expectedChildrenCount` | 预期子节点总数（扫描后设置） |
| `_completedChildrenCount` | 已完成子节点计数 |
| `_isMarkedComplete` | 防止重复上报的标志 |

#### 双队列架构

```csharp
// 两个并发队列
private ConcurrentQueue<FileItem> _pendingQueue = new();     // 待处理队列（测试密码）
private ConcurrentQueue<FileItem> _extractQueue = new();     // 待解压队列（已测试密码）

// 信号量唤醒机制
private ManualResetEventSlim _pendingQueueSignal = new(false);
private ManualResetEventSlim _extractQueueSignal = new(false);

// 线程状态标识
private bool _isTesting = false;
private bool _isExtracting = false;

// 任务完成信号
private TaskCompletionSource<bool> _testCompletionSource = new();
private TaskCompletionSource<bool> _extractCompletionSource = new();

// 密码映射表（线程安全）
private ConcurrentDictionary<string, string?> _passwordMap = new();
```

### 2.3 状态机设计

#### WorkState 枚举

```csharp
public enum WorkState
{
    Idle,        // 空闲状态
    Testing,     // 密码测试中
    Extracting,  // 解压进行中
    Completed,   // 全部完成
    Failed       // 失败/已停止
}
```

#### 状态转换流程

```
         ┌─────────────────────────────────────┐
         │                                     │
         ▼                                     │
    ┌─────────┐    用户点击"开始"     ┌──────────────┐
    │  Idle   │ ──────────────────► │  Testing     │
    └─────────┘                     └──────┬───────┘
         ▲                                 │
         │                                 │ 自动模式
         │                                 ▼
         │                          ┌──────────────┐
         │                          │ Extracting   │
         │                          └──────┬───────┘
         │                                 │
         │ 完成/失败                       │ 完成
         │                                 ▼
         │                          ┌──────────────┐
         └──────────────────────────┤  Completed   │
                                    └──────────────┘
                                          │
                                    用户点击"开始"
                                          │
                                          ▼
                                    ┌──────────────┐
                                    │    Idle      │
                                    └──────────────┘

任何运行中状态 (Testing/Extracting)
         │
         │ 用户点击"停止" 或 异常
         ▼
    ┌─────────┐
    │ Failed  │
    └─────────┘
```

#### UI 状态更新

```csharp
private void UpdateUiState()
{
    switch (_currentState)
    {
        case WorkState.Idle:
        case WorkState.Completed:
        case WorkState.Failed:
            BtnStartExtract.Content = "开始解压";
            BtnStartExtract.IsEnabled = true;
            break;
            
        case WorkState.Testing:
        case WorkState.Extracting:
            BtnStartExtract.Content = "停止";
            BtnStartExtract.IsEnabled = true;
            break;
    }
}
```

---

## 3. 核心机制

### 3.1 文件检测与加载流程

#### 3.1.1 拖拽处理入口

**触发时机：** 用户拖拽文件或文件夹到窗口

**处理流程：**

```
Window_Drop 事件
    ↓
遍历所有拖拽路径
    ├─ 如果是文件 → ProcessSingleFile()
    │   ├─ 检查黑名单 → IsBlacklistedFile()
    │   │   └─ 匹配 → DeleteBlacklistedFile() → 跳过
    │   ├─ 检测压缩文件 → IsArchiveFile()
    │   │   ├─ 是分卷压缩包？→ DetectMultiVolumeArchive()
    │   │   │   └─ 是 → 只添加主卷（合并分卷信息）
    │   │   └─ 否 → AddFileToList()
    │   └─ 添加到 _pendingQueue
    │
    └─ 如果是文件夹 → ScanDirectoryForArchives()
        ├─ 递归扫描所有文件（SearchOption.AllDirectories）
        ├─ 对每个文件：
        │   ├─ 检查黑名单 → 删除并跳过
        │   ├─ 扩展名初步筛选 → IsArchiveFileByExtension()
        │   └─ 魔术数验证 → IsArchiveFileByMagicNumber()
        ├─ 分卷合并处理（按目录分组）
        └─ 添加到 _pendingQueue
```

**关键特性：**
- **单文件模式**：只处理当前文件及其分卷，不扫描同级目录
- **文件夹模式**：递归扫描内部所有子文件夹，但仅限该文件夹内部
- **双重验证**：扩展名 + 文件头魔术数，避免误识别
- **分卷自动合并**：检测到分卷时只添加主卷，存储完整分卷信息

#### 3.1.2 压缩文件识别策略

**三级检测机制：**

1. **扩展名匹配**（快速筛选）
   ```csharp
   private bool IsArchiveFileByExtension(string filePath)
   {
       string ext = Path.GetExtension(filePath).ToLowerInvariant();
       return _settings.GetArchiveExtensions().Contains(ext);
   }
   ```

2. **魔术数验证**（准确识别）
   ```csharp
   private bool IsArchiveFileByMagicNumber(string filePath)
   {
       // 读取文件头字节，匹配常见压缩格式签名
       // 7z: 37 7A BC AF 27 1C
       // RAR: 52 61 72 21 1A 07
       // ZIP: 50 4B 03 04
       // ...
   }
   ```

3. **综合判断**
   ```csharp
   private bool IsArchiveFile(string filePath)
   {
       // 先检查扩展名
       if (IsArchiveFileByExtension(filePath))
           return true;
       
       // 再检查魔术数
       return IsArchiveFileByMagicNumber(filePath);
   }
   ```

#### 3.1.3 黑名单过滤机制

**配置方式：**
在 `AppSettings` 中维护正则表达式列表，例如：
```json
{
  "BlacklistPatterns": [
    "^\\..*",          // 隐藏文件（以.开头）
    "thumbs\\.db$",    // Windows 缩略图缓存
    "desktop\\.ini$"   // Windows 桌面配置
  ]
}
```

**应用时机：**
1. **拖拽加载时**：立即删除匹配的文件，不加入队列
2. **解压后扫描时**：发现黑名单文件直接删除，不加入待处理队列
3. **智能路径扁平化前**：清理解压目录内的黑名单文件，避免影响扁平化条件

**实现逻辑：**
```csharp
private bool IsBlacklistedFile(string filePath, out string matchedPattern)
{
    var patterns = _settings.GetBlacklistPatterns();
    string fileName = Path.GetFileName(filePath);
    
    foreach (var pattern in patterns)
    {
        if (Regex.IsMatch(fileName, pattern, RegexOptions.IgnoreCase))
        {
            matchedPattern = pattern;
            return true;
        }
    }
    
    matchedPattern = string.Empty;
    return false;
}

private void DeleteBlacklistedFile(string filePath, string matchedPattern)
{
    try
    {
        File.Delete(filePath);
        AppendLog($"[黑名单] 已删除: {Path.GetFileName(filePath)} (匹配: {matchedPattern})", ConsoleColor.Yellow);
    }
    catch (Exception ex)
    {
        AppendLog($"[黑名单] 删除失败: {ex.Message}", ConsoleColor.Red);
    }
}
```

---

### 3.2 双队列异步架构

#### 3.2.1 设计理念

将密码测试和解压流程分离为两个独立的线程和队列，实现：
- ✅ 清晰的职责分工
- ✅ 高效的并发处理
- ✅ 灵活的扩展能力

#### 3.2.2 工作流程图

```
┌─────────────┐
│  拖拽文件    │
└──────┬──────┘
       │
       ▼
┌─────────────────────────────────────────────────────┐
│              待处理队列 (Pending Queue)               │
│  - 新添加的文件                                      │
│  - 解压出来的子文件                                   │
│  - 状态: 等待处理                                     │
└──────┬──────────────────────────────────────────────┘
       │
       ▼
┌─────────────────────────────────────────────────────┐
│              测试线程 (Test Thread)                   │
│                                                       │
│  while (有文件) {                                    │
│    file = pendingQueue.TryDequeue()                  │
│                                                       │
│    if (!IsArchiveFile(file)) {                       │
│      标记为"非压缩文件，跳过"                          │
│      检查父项完成                                     │
│      continue                                        │
│    }                                                  │
│                                                       │
│    // 隐写容器检测（特殊模式）                         │
│    if (IsStegoDetectionActiveForFile(file)) {        │
│      记录无密码到 _passwordMap                        │
│      extractQueue.Enqueue(file)                      │
│      continue                                        │
│    }                                                  │
│                                                       │
│    // 步骤1：测试无密码                               │
│    success = TestPasswordAsync(file, "")             │
│    if (success) {                                    │
│      foundPassword = null                            │
│    } else {                                          │
│      // 步骤2：遍历密码本                             │
│      foreach (password in passwords) {               │
│        success = TestPasswordAsync(file, password)   │
│        if (success) {                                │
│          foundPassword = password                    │
│          RecordPasswordUsage(password)               │
│          break                                       │
│        }                                             │
│      }                                               │
│    }                                                  │
│                                                       │
│    // 记录密码到映射表                                │
│    _passwordMap.Add(file.FilePath, foundPassword)    │
│    file.Status = "密码正确/无密码"                    │
│    extractQueue.Enqueue(file)  // 加入待解压队列      │
│    WakeupExtractThread()     // 唤醒解压线程          │
│  }                                                    │
└──────┬──────────────────────────────────────────────┘
       │
       ▼
┌─────────────────────────────────────────────────────┐
│              待解压队列 (Extract Queue)               │
│  - 密码测试完成的文件                                 │
│  - 包含密码信息                                       │
│  - 状态: 等待解压                                     │
└──────┬──────────────────────────────────────────────┘
       │
       ▼
┌─────────────────────────────────────────────────────┐
│            解压线程 (Extract Thread)                  │
│            （多线程并发，可配置数量）                  │
│                                                       │
│  for (int i = 0; i < ThreadCount; i++) {           │
│    Task.Run(async () => {                           │
│      while (有文件) {                               │
│        file = extractQueue.TryDequeue()             │
│                                                       │
│        // 获取锁防止重复处理                          │
│        if (!file.TryLock()) {                       │
│          extractQueue.Enqueue(file)  // 重新入队     │
│          continue                                   │
│        }                                              │
│                                                       │
│        try {                                         │
│          // 从密码映射表获取密码                      │
│          password = _passwordMap[file.FilePath]      │
│                                                       │
│          // 执行解压                                  │
│          result = ExtractAsync(                      │
│            file.FilePath,                            │
│            outputDir,                                │
│            password,                                 │
│            onProgress: updateUI,                     │
│            useHashTypeMode: IsStegoMode              │
│          )                                            │
│                                                       │
│          if (result.Success) {                       │
│            // 扫描解压后的文件                        │
│            ScanExtractedFilesForArchives(            │
│              outputDir,                              │
│              existingFilesBeforeExtract              │
│            )                                          │
│            // 这会添加子压缩包到树形结构              │
│            // 并加入 _pendingQueue                    │
│            WakeupTestThread()  // 唤醒测试线程        │
│          } else {                                    │
│            file.Status = "解压失败"                  │
│            UpdateParentStatusWhenChildrenComplete()  │
│          }                                            │
│        } finally {                                   │
│          file.ReleaseLock()                          │
│        }                                              │
│      }                                                │
│    })                                                 │
│  }                                                    │
└──────┬──────────────────────────────────────────────┘
       │
       ▼
┌─────────────────────────────────────────────────────┐
│         监控完成 (MonitorProcessingComplete)          │
│                                                       │
│  while (true) {                                     │
│    await Task.WhenAll(                              │
│      _testCompletionSource.Task,                    │
│      _extractCompletionSource.Task                  │
│    )                                                  │
│                                                       │
│    if (!_isTesting && !_isExtracting                │
│        && _extractQueue.IsEmpty                     │
│        && _pendingQueue.IsEmpty) {                  │
│      AppendLog("所有处理流程完成")                   │
│      CheckAndTriggerBatchSmartPathProcessing()      │
│      break                                          │
│    }                                                  │
│  }                                                    │
└─────────────────────────────────────────────────────┘
```

#### 3.2.3 核心方法

**StartDualQueueProcessing()** - 启动双队列处理流程

```csharp
private void StartDualQueueProcessing()
{
    if (_isTesting || _isExtracting)
        return;

    // 重置完成信号
    _testCompletionSource = new TaskCompletionSource<bool>();
    _extractCompletionSource = new TaskCompletionSource<bool>();

    // 启动测试线程
    StartTestThread();
    
    // 启动解压线程
    StartExtractThread();
    
    // 监听完成事件
    MonitorProcessingComplete();
}
```

**StartTestThread()** - 测试线程

职责：
- 从 `_pendingQueue` 取出文件
- 判断是否是压缩文件
- 测试密码（先测试无密码，再测试密码本）
- 记录密码到 `_passwordMap`
- 将文件加入 `_extractQueue`

**StartExtractThread()** - 解压线程

职责：
- 从 `_extractQueue` 取出文件
- 获取密码（从 `_passwordMap`）
- 执行解压操作
- 扫描输出目录，发现新的压缩文件
- 将新文件添加到树形结构并加入 `_pendingQueue`
- 检查父项完成

特性：
- 多线程并发处理（根据 `ThreadCount` 设置）
- 使用 `TryLock()` 防止重复处理
- 循环等待新文件（使用信号量机制）

---

### 3.3 密码测试详细流程

#### 3.3.1 测试策略

**优先级顺序：**
1. **隐写容器检测**（如果启用）→ 直接标记为无密码
2. **无密码测试** → 尝试空密码
3. **一次性密码** → 用户临时输入的密码
4. **永久密码本** → 按综合评分排序的密码列表

**综合评分算法：**
```csharp
public double Score
{
    get
    {
        double usageScore = UsageCount * 100;  // 使用次数权重
        
        // 时间衰减因子
        double timeScore = 0;
        var daysSinceLastUsed = (DateTime.Now - LastUsedTime).TotalDays;
        
        if (daysSinceLastUsed < 1)
            timeScore = 10000;  // 1小时内
        else if (daysSinceLastUsed < 7)
            timeScore = 5000;   // 7天内
        else if (daysSinceLastUsed < 30)
            timeScore = 1000;   // 30天内
        else
            timeScore = 100;    // 30天以上
        
        return usageScore + timeScore;
    }
}
```

#### 3.3.2 测试实现

```csharp
private async Task ProcessPendingFile(FileItem fileItem, SevenZipExtractor extractor, 
                                      List<string> passwords, CancellationToken token)
{
    Dispatcher.Invoke(() => fileItem.Status = "正在测试密码...");

    // 步骤1：判断是否是压缩文件
    if (!IsArchiveFile(fileItem.FilePath))
    {
        Dispatcher.Invoke(() => fileItem.Status = "非压缩文件，跳过");
        if (fileItem.Parent != null)
            UpdateParentStatusWhenChildrenComplete(fileItem);
        return;
    }

    string? foundPassword = null;
    
    // 步骤2：隐写容器检测
    bool stegoMode = IsStegoDetectionActiveForFile(fileItem.FilePath);
    if (stegoMode)
    {
        _passwordMap.Add(fileItem.FilePath, null);
        Dispatcher.Invoke(() =>
        {
            fileItem.FoundPassword = null;
            fileItem.Status = "隐写容器（无需密码）";
        });
        _extractQueue.Enqueue(fileItem);
        WakeupExtractThread();
        return;
    }

    // 步骤3：尝试无密码
    Dispatcher.Invoke(() =>
    {
        TxtStatusCurrentPassword.Text = "测试中的密码: (无密码)";
        fileItem.Status = "测试: 无密码";
    });

    bool noPasswordSuccess = await extractor.TestPasswordAsync(
        fileItem.FilePath, 
        "",  // 空密码
        onOutput: (msg) => { },
        cancellationToken: token);

    if (noPasswordSuccess)
    {
        foundPassword = null;
    }
    else
    {
        // 步骤4：测试密码本
        int testedCount = 0;
        foreach (var password in passwords)
        {
            if (token.IsCancellationRequested)
                break;

            testedCount++;
            Dispatcher.Invoke(() =>
            {
                TxtStatusCurrentPassword.Text = $"测试中的密码: {password}";
                fileItem.Status = $"测试: {password} ({testedCount}/{passwords.Count})";
            });

            bool isValid = await extractor.TestPasswordAsync(
                fileItem.FilePath, 
                password,
                onOutput: (msg) => { },
                cancellationToken: token);

            if (isValid)
            {
                foundPassword = password;
                _settings.RecordPasswordUsage(password);  // 记录使用次数
                break;
            }

            await Task.Delay(50, token);  // 避免过于频繁的调用
        }
    }

    // 步骤5：记录到密码映射表
    _passwordMap.Add(fileItem.FilePath, foundPassword);

    // 步骤6：更新UI
    Dispatcher.Invoke(() =>
    {
        fileItem.FoundPassword = foundPassword;
        TxtStatusCurrentPassword.Text = "测试中的密码: 无";
        
        if (foundPassword != null)
        {
            fileItem.Status = $"密码正确: {foundPassword}";
        }
        else
        {
            fileItem.Status = "无密码";
        }
    });

    // 步骤7：将文件加入待解压队列
    _extractQueue.Enqueue(fileItem);
    AppendLog($"[{fileItem.FileName}] 已加入待解压队列", ConsoleColor.Gray);
    
    // 唤醒解压线程
    WakeupExtractThread();

    await Task.Delay(100, token);
}
```

#### 3.3.3 性能优化

**问题：** 频繁调用 7z 命令行进行密码测试会导致性能瓶颈。

**解决方案：**
1. **异步调用**：使用 `async/await` 避免阻塞 UI
2. **延迟控制**：每次测试后 `Task.Delay(50)` 避免过于频繁
3. **快速失败**：先测试无密码，成功后立即返回
4. **编码优化**：统一使用 UTF-8 编码读取 7z 输出，避免 GBK 解码错误

---

### 3.4 解压详细流程

#### 3.4.1 解压执行

```csharp
private async Task ProcessExtractFile(FileItem fileItem, SevenZipExtractor extractor, 
                                      int taskId, CancellationToken token)
{
    // 从密码映射表获取密码
    if (!_passwordMap.TryGetValue(fileItem.FilePath, out var password))
    {
        Dispatcher.Invoke(() => fileItem.Status = "跳过: 未找到密码");
        if (fileItem.Parent != null)
            UpdateParentStatusWhenChildrenComplete(fileItem);
        else
            CheckAndMarkParentComplete(fileItem);
        return;
    }

    Dispatcher.Invoke(() => fileItem.Status = "正在解压...");
    
    string outputDir = GetOutputDirectory(fileItem.FilePath);
    AppendLog($"\n[线程 {taskId}] 解压: {fileItem.FileName}", ConsoleColor.White);
    AppendLog($"  输出目录: {outputDir}", ConsoleColor.Gray);
    AppendLog($"  密码: {password ?? "(无密码)"}", ConsoleColor.Gray);

    // 记录解压前输出目录中已存在的文件，避免扫描到无关文件
    var existingFilesBeforeExtract = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    if (Directory.Exists(outputDir))
    {
        try
        {
            var existingFiles = Directory.GetFiles(outputDir, "*.*", SearchOption.AllDirectories);
            foreach (var f in existingFiles)
            {
                existingFilesBeforeExtract.Add(f);
            }
        }
        catch { /* 忽略访问权限等问题 */ }
    }

    try
    {
        var result = await extractor.ExtractAsync(
            fileItem.FilePath,
            outputDir,
            password,
            onProgress: (msg) => AppendLog($"  {msg}", ConsoleColor.Gray),
            onPercentChanged: (percent) =>
            {
                Dispatcher.Invoke(() =>
                {
                    ProgressExtract.Value = percent;
                    TxtExtractProgress.Text = $"{percent}%";
                });
            },
            showCliWindow: _settings.ShowCliWindow,
            cancellationToken: token,
            useHashTypeMode: IsStegoDetectionActiveForFile(fileItem.FilePath));

        if (result.Success)
        {
            // 重置进度条
            Dispatcher.Invoke(() =>
            {
                ProgressExtract.Value = 0;
                TxtExtractProgress.Text = "0%";
            });
            
            // 标记自身解压成功
            fileItem.SetSelfExtractResult(true);

            // 从密码映射表中移除已完成处理的文件记录
            _passwordMap.Remove(fileItem.FilePath);
            AppendLog($"[线程 {taskId}] {fileItem.FileName}: 已从密码映射表清除", ConsoleColor.Gray);

            // 扫描解压后的文件，检测是否包含新的压缩文件
            await ScanExtractedFilesForArchives(outputDir, taskId, fileItem, existingFilesBeforeExtract);
            
            // 根据是否有子节点来决定显示状态
            if (fileItem.Children.Count > 0)
            {
                var passwordInfo = password != null
                    ? $" (密码: {password})"
                    : " (无密码)";
                Dispatcher.Invoke(() =>
                {
                    fileItem.Status = $"等待子节点完成{passwordInfo} ({fileItem.GetProgressInfo()})";
                });
                AppendLog($"[{fileItem.FileName}] 已添加 {fileItem.Children.Count} 个子压缩包到待处理队列，等待递归处理...", ConsoleColor.Cyan);
            }
        }
        else
        {
            // 解压失败
            Dispatcher.Invoke(() =>
            {
                ProgressExtract.Value = 0;
                TxtExtractProgress.Text = "0%";
            });
            
            Dispatcher.Invoke(() => fileItem.Status = $"解压失败: {result.Message}");
            AppendLog($"[线程 {taskId}] {fileItem.FileName}: 解压失败 - {result.Message}", ConsoleColor.Red);
            
            // 标记自身解压失败
            fileItem.SetSelfExtractResult(false);
            
            // 解压失败时不处理原文件，只检查父项状态
            if (fileItem.Parent != null)
                UpdateParentStatusWhenChildrenComplete(fileItem);
            else
                CheckAndMarkParentComplete(fileItem);
        }
    }
    catch (OperationCanceledException)
    {
        Dispatcher.Invoke(() => fileItem.Status = "已取消");
        AppendLog($"[线程 {taskId}] {fileItem.FileName}: 已取消", ConsoleColor.Yellow);
        
        if (fileItem.Parent != null)
            UpdateParentStatusWhenChildrenComplete(fileItem);
        else
            CheckAndMarkParentComplete(fileItem);
        throw;
    }
    catch (Exception ex)
    {
        Dispatcher.Invoke(() => fileItem.Status = $"异常: {ex.Message}");
        AppendLog($"[线程 {taskId}] {fileItem.FileName}: 异常 - {ex.Message}", ConsoleColor.Red);
        
        if (fileItem.Parent != null)
            UpdateParentStatusWhenChildrenComplete(fileItem);
        else
            CheckAndMarkParentComplete(fileItem);
    }
}
```

#### 3.4.2 解压后文件扫描

**关键逻辑：**

```csharp
private async Task ScanExtractedFilesForArchives(string outputDir, int taskId, 
                                                  FileItem? parentFileItem = null, 
                                                  HashSet<string>? existingFilesBeforeExtract = null)
{
    try
    {
        if (!Directory.Exists(outputDir))
            return;

        // 递归查找输出目录中解压出来的新压缩文件
        var archiveExtensions = _settings.GetArchiveExtensions();
        var allArchiveFiles = new List<string>();
        
        // 1. 扫描具有压缩文件扩展名的文件
        foreach (var ext in archiveExtensions)
        {
            string searchPattern = ext.StartsWith(".") ? $"*{ext}" : $"*.{ext}";
            try
            {
                var files = Directory.GetFiles(outputDir, searchPattern, SearchOption.AllDirectories);
                allArchiveFiles.AddRange(files);
            }
            catch { /* 忽略访问权限等问题 */ }
        }
        
        // 2. 对所有其他文件使用魔术数检测（不依赖扩展名）
        var allFiles = Directory.GetFiles(outputDir, "*.*", SearchOption.AllDirectories);
        foreach (var file in allFiles)
        {
            if (allArchiveFiles.Contains(file))
                continue;
                
            if (IsArchiveFile(file))
                allArchiveFiles.Add(file);
        }
        
        // 去重
        allArchiveFiles = allArchiveFiles.Distinct().ToList();

        // 过滤掉解压前已存在的文件，只保留新解压的文件
        if (existingFilesBeforeExtract != null && existingFilesBeforeExtract.Count > 0)
        {
            var newlyExtractedFiles = allArchiveFiles.Where(f => !existingFilesBeforeExtract.Contains(f)).ToList();
            allArchiveFiles = newlyExtractedFiles;
        }

        if (allArchiveFiles.Count == 0)
        {
            AppendLog($"[线程 {taskId}] 未发现新的压缩文件", ConsoleColor.Gray);
            if (parentFileItem != null)
                FinishLeafAfterScanNoChildArchivesToQueue(parentFileItem, taskId);
            return;
        }

        AppendLog($"[线程 {taskId}] 发现 {allArchiveFiles.Count} 个新的压缩文件", ConsoleColor.Cyan);

        int addedCount = 0;
        foreach (var archiveFile in allArchiveFiles)
        {
            // 检查黑名单 - 如果是黑名单文件则直接删除，不加入处理队列
            if (IsBlacklistedFile(archiveFile, out string matchedPattern))
            {
                try
                {
                    File.Delete(archiveFile);
                    AppendLog($"[线程 {taskId}] [黑名单] 已删除: {Path.GetFileName(archiveFile)} (匹配模式: {matchedPattern})", ConsoleColor.Yellow);
                }
                catch (Exception delEx)
                {
                    AppendLog($"[线程 {taskId}] [黑名单] 删除失败: {Path.GetFileName(archiveFile)} - {delEx.Message}", ConsoleColor.Red);
                }
                continue;  // 跳过后续处理
            }
            
            // 检查是否已在列表中
            FileItem? existingItem = null;
            Dispatcher.Invoke(() =>
            {
                existingItem = FindFileItemInTree(archiveFile);
            });

            if (existingItem != null)
                continue;

            // 创建新的文件项
            var fileInfo = new FileInfo(archiveFile);
            var fileItem = new FileItem
            {
                FileName = fileInfo.Name,
                FilePath = archiveFile,
                Status = "等待处理",
                FileSize = fileInfo.Length,
                Parent = parentFileItem,
                TaskId = taskId
            };

            Dispatcher.Invoke(() =>
            {
                if (parentFileItem != null)
                {
                    parentFileItem.Children.Add(fileItem);
                }
                else
                {
                    _fileList.Add(fileItem);
                }
            });

            addedCount++;
            AppendLog($"[线程 {taskId}] 添加新压缩文件: {fileItem.FileName}", ConsoleColor.Green);
        }

        if (addedCount > 0)
        {
            AppendLog($"[线程 {taskId}] 共添加 {addedCount} 个新压缩文件到待处理队列", ConsoleColor.Cyan);
            
            // 设置父节点的预期子节点数量
            if (parentFileItem != null)
            {
                parentFileItem.SetExpectedChildrenCount(addedCount);
            }
            
            // 将新文件加入待处理队列
            Dispatcher.Invoke(() =>
            {
                var children = parentFileItem?.Children ?? new ObservableCollection<FileItem>();
                foreach (var child in children)
                {
                    if (child.Status == "等待处理")
                    {
                        _pendingQueue.Enqueue(child);
                    }
                }
            });
            
            // 唤醒测试线程
            WakeupTestThread();
        }
        else
        {
            // 有候选但未入队（黑名单删除、已在树中等）：须走与「零候选」相同的叶子收尾
            if (parentFileItem != null)
            {
                FinishLeafAfterScanNoChildArchivesToQueue(parentFileItem, taskId, "(候选未入队，可能已按黑名单删除或跳过)");
            }
        }
    }
    catch (Exception ex)
    {
        AppendLog($"[线程 {taskId}] 扫描解压文件失败: {ex.Message}", ConsoleColor.Red);
    }
}
```

**关键特性：**
- **双重检测**：扩展名 + 魔术数，确保准确识别
- **去重过滤**：通过 `existingFilesBeforeExtract` 排除旧文件
- **黑名单清理**：发现黑名单文件立即删除，不加入队列
- **父子关系维护**：正确设置 `Parent` 字段，构建完整树形结构
- **反向传播触发**：设置预期子节点数，启动状态同步机制

---

### 3.2 信号量唤醒机制

#### 问题背景

在双队列架构中，如果测试线程或解压线程在队列为空时直接退出，会导致：

```
时间线：
T1: 解压线程发现新文件 → 加入 _pendingQueue
T2: 测试线程已经退出（因为之前队列为空）
T3: ❌ 新文件永远不会被处理！
```

#### 解决方案

使用 `ManualResetEventSlim` 实现线程休眠和唤醒机制。

#### 核心原理

```
┌─────────────────────────────────────────────┐
│           线程等待逻辑                        │
│                                              │
│  while (!token.IsCancellationRequested)     │
│  {                                          │
│      if (queue.TryDequeue(out item))        │
│      {                                      │
│          处理文件...                         │
│      }                                      │
│      else                                   │
│      {                                      │
│          // 队列为空                        │
│          ↓                                  │
│          等待信号（最多30秒）                 │
│          ↓                                  │
│          if (收到信号)                       │
│              继续循环                        │
│          else if (超时 && 仍为空)            │
│              break  // 真正退出              │
│      }                                      │
│  }                                          │
└─────────────────────────────────────────────┘

┌─────────────────────────────────────────────┐
│           入队唤醒逻辑                        │
│                                              │
│  queue.Enqueue(item);                       │
│  ↓                                          │
│  _queueSignal.Set();  // 立即唤醒           │
│  ↓                                          │
│  等待的线程被唤醒                            │
│  ↓                                          │
│  继续处理新文件                              │
└─────────────────────────────────────────────┘
```

#### 实现代码

**等待逻辑**

```csharp
while (!token.IsCancellationRequested)
{
    if (_pendingQueue.TryDequeue(out var fileItem))
    {
        // 处理文件...
        await ProcessPendingFile(fileItem, extractor, passwords, token);
    }
    else
    {
        // 队列为空，等待信号或超时
        AppendLog("[测试线程] 队列为空，等待新任务...", ConsoleColor.Gray);
        
        // 等待信号（最多等待30秒）或取消请求
        bool signaled = false;
        try
        {
            signaled = await Task.Run(() => 
                _pendingQueueSignal.Wait(TimeSpan.FromSeconds(30), token), token);
        }
        catch (OperationCanceledException)
        {
            break;
        }
        
        if (!signaled)
        {
            // 超时或取消，再次检查是否真的为空
            if (_pendingQueue.IsEmpty)
            {
                AppendLog("[测试线程] 超时且队列为空，测试线程完成", ConsoleColor.Gray);
                break;
            }
        }
        
        // 重置信号，准备下一次等待
        _pendingQueueSignal.Reset();
    }
}
```

**唤醒方法**

```csharp
/// <summary>
/// 唤醒测试线程（当有待处理文件加入队列时调用）
/// </summary>
private void WakeupTestThread()
{
    _pendingQueueSignal.Set();
    AppendLog("[系统] 已唤醒测试线程", ConsoleColor.Gray);
}

/// <summary>
/// 唤醒解压线程（当有待解压文件加入队列时调用）
/// </summary>
private void WakeupExtractThread()
{
    _extractQueueSignal.Set();
    AppendLog("[系统] 已唤醒解压线程", ConsoleColor.Gray);
}
```

#### 调用时机

必须在所有入队操作后调用唤醒方法：

1. **拖拽文件后**
   ```csharp
   _pendingQueue.Enqueue(fileItem);
   WakeupTestThread();
   ```

2. **测试完成后加入待解压队列**
   ```csharp
   _extractQueue.Enqueue(fileItem);
   WakeupExtractThread();
   ```

3. **解压后发现新文件**
   ```csharp
   _pendingQueue.Enqueue(newFile);
   WakeupTestThread();
   ```

#### 优势对比

| 特性 | 旧方案 (Task.Delay) | 新方案 (ManualResetEventSlim) |
|------|-------------------|----------------------------|
| **响应速度** | 慢 (100-500ms) | 快 (< 1ms) |
| **CPU占用** | 高 (频繁轮询) | 低 (阻塞等待) |
| **可靠性** | 低 (可能错过文件) | 高 (立即唤醒) |
| **资源消耗** | 高 | 低 |

**性能数据：**

假设场景：每5分钟有一个新文件加入队列

```
旧方案 (Task.Delay 500ms):
- 平均延迟: 250ms
- CPU占用: ~5% (持续轮询)
- 24小时唤醒次数: 172,800次

新方案 (ManualResetEventSlim):
- 平均延迟: < 1ms
- CPU占用: ~0% (阻塞等待)
- 24小时唤醒次数: 288次 (仅在实际有文件时)

性能提升:
- 响应速度: 250倍
- CPU占用: 降低99%
- 唤醒次数: 减少99.8%
```

---

### 3.5 反向传播机制（父子状态同步）

#### 问题背景

在树形结构的递归解压中，存在以下问题：

```
Root (顶级压缩包)
├─ file1.zip (子压缩包1) - 状态: 解压成功 ✓
├─ file2.rar (子压缩包2) - 状态: 解压成功 ✓
└─ file3.7z (子压缩包3) - 状态: 解压成功 ✓

但是 Root 的状态仍然是: "等待处理" ❌
```

**问题：** 所有子节点都完成了，但父节点的状态没有更新！

#### 解决方案

实现**反向传播机制**（Bottom-Up Propagation）：

```
子节点完成 → 上报父节点 → 父节点检查所有子节点 → 如果都完成则标记自己完成 → 继续向上上报
```

#### 核心设计

**FileItem 增强字段**

```csharp
// 子节点完成状态跟踪（用于反向传播）
private int _expectedChildrenCount = 0;      // 预期的子节点总数
private int _completedChildrenCount = 0;     // 已完成的子节点数
private bool _isMarkedComplete = false;      // 是否已标记为完成（防止重复上报）
```

**关键方法**

```csharp
/// <summary>
/// 设置预期的子节点数量，在扫描完解压目录后调用
/// </summary>
public void SetExpectedChildrenCount(int count)
{
    _expectedChildrenCount = count;
    _completedChildrenCount = 0;
    _isMarkedComplete = false;
}

/// <summary>
/// 标记一个子节点完成，并返回是否所有子节点都已完成
/// </summary>
public bool MarkChildComplete()
{
    lock (_lockObject)
    {
        if (_isMarkedComplete)
            return false;  // 已经标记完成，不再处理
            
        _completedChildrenCount++;
        
        // 检查是否所有子节点都已完成
        bool allCompleted = _completedChildrenCount >= _expectedChildrenCount 
                           && _expectedChildrenCount > 0;
        
        if (allCompleted)
        {
            _isMarkedComplete = true;
        }
        
        return allCompleted;
    }
}

/// <summary>
/// 获取完成进度信息，用于显示
/// </summary>
public string GetProgressInfo()
{
    return $"{_completedChildrenCount}/{_expectedChildrenCount}";
}
```

**UpdateParentStatusWhenChildrenComplete() - 核心反向传播方法**

```csharp
private void UpdateParentStatusWhenChildrenComplete(FileItem childItem)
{
    var parentItem = childItem.Parent;
    if (parentItem == null)
        return;  // 没有父节点，不需要上报

    // 步骤1：标记父节点的一个子节点完成
    bool allCompleted = parentItem.MarkChildComplete();
    
    AppendLog($"[{parentItem.FileName}] 子节点完成进度: {parentItem.GetProgressInfo()}");

    // 步骤2：如果所有子节点都已完成，更新父节点状态并继续向上上报
    if (allCompleted)
    {
        // 2.1 更新父节点状态为完成
        var passwordInfo = parentItem.FoundPassword != null 
            ? $" (密码: {parentItem.FoundPassword})" 
            : " (无密码)";
        
        Dispatcher.Invoke(() =>
        {
            parentItem.Status = $"解压成功{passwordInfo} [包含 {parentItem.Children.Count} 个子压缩包]";
        });
        
        AppendLog($"[{parentItem.FileName}] ✓ 所有子压缩包处理完成，父压缩包标记为完成");
        
        // 2.2 从密码映射表中移除
        _passwordMap.Remove(parentItem.FilePath);
        
        // 2.3 递归处理：向上一级上报
        if (parentItem.Parent != null)
        {
            AppendLog($"[{parentItem.FileName}] 向上一级上报完成状态...");
            UpdateParentStatusWhenChildrenComplete(parentItem);  // 传入父节点作为子节点
        }
        else
        {
            // 到达顶级
            AppendLog($"[{parentItem.FileName}] 已到达顶级，整个树形结构处理完成");
        }
    }
    else
    {
        // 步骤3：还有子节点未完成，更新状态显示进度
        Dispatcher.Invoke(() =>
        {
            parentItem.Status = $"等待子节点完成 ({parentItem.GetProgressInfo()})";
        });
        AppendLog($"[{parentItem.FileName}] 还有子节点未完成，当前状态: {parentItem.Status}");
    }
}
```

#### 工作流程图

```
解压 file1.zip
    ↓
扫描输出目录
    ↓
发现 3 个子文件: sub1.rar, sub2.7z, sub3.zip
    ↓
parentFileItem.SetExpectedChildrenCount(3)
    ↓
将 3 个子文件加入待处理队列
    ↓
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
sub1.rar 处理完成
    ↓
UpdateParentStatusWhenChildrenComplete(sub1)
    ↓
parent.MarkChildComplete() → 返回 false (1/3)
    ↓
parent.Status = "等待子节点完成 (1/3)"
    ↓
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
sub2.7z 处理完成
    ↓
UpdateParentStatusWhenChildrenComplete(sub2)
    ↓
parent.MarkChildComplete() → 返回 false (2/3)
    ↓
parent.Status = "等待子节点完成 (2/3)"
    ↓
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
sub3.zip 处理完成
    ↓
UpdateParentStatusWhenChildrenComplete(sub3)
    ↓
parent.MarkChildComplete() → 返回 true (3/3) ✓
    ↓
parent.Status = "解压成功 (密码: xxx) [包含 3 个子压缩包]"
    ↓
_passwordMap.Remove(parent.FilePath)
    ↓
parent.Parent != null? 
    ├─ 是 → UpdateParentStatusWhenChildrenComplete(parent)  // 继续向上
    └─ 否 → "已到达顶级，整个树形结构处理完成" ✓
```

#### 调用时机

必须在以下位置调用 `UpdateParentStatusWhenChildrenComplete(fileItem)`：

1. **非压缩文件跳过时**
   ```csharp
   if (!IsArchiveFile(fileItem.FilePath))
   {
       fileItem.Status = "非压缩文件，跳过";
       if (fileItem.Parent != null)
           UpdateParentStatusWhenChildrenComplete(fileItem);
       return;
   }
   ```

2. **未找到密码跳过时**
3. **解压失败时**
4. **取消时**
5. **异常时**

**注意：** 必须传入**子节点**，不是父节点！

```csharp
// ❌ 错误：传入父节点
UpdateParentStatusWhenChildrenComplete(fileItem.Parent);

// ✅ 正确：传入子节点
UpdateParentStatusWhenChildrenComplete(fileItem);
```

---

### 3.6 智能路径扁平化处理

#### 3.6.1 业务逻辑

**目标：** 自动简化多层嵌套的文件夹结构，提升用户体验。

**触发条件：**
1. **启用配置**：`EnableSmartPathProcessing = true`
2. **输出模式**：`OutputMode = ArchiveFolder`（以压缩包名命名文件夹）
3. **完成时机**：所有顶级文件项处理完成后统一执行

**扁平化规则：**
- 当前目录恰好包含 **一个子文件夹** 且 **没有其他文件**
- 将子文件夹内容提升到父目录层级
- 根据配置决定最终文件夹名称（拼接模式 / 智能选择模式）

#### 3.6.2 执行流程

```
CheckAndTriggerBatchSmartPathProcessing()
    ↓
检查是否所有顶级文件项都已完成
    ↓
收集所有解压生成的文件夹
    ↓
对每个解压文件夹执行 ProcessSmartPathBatch()
    ├─ DeleteBlacklistedFilesUnderDirectory()  // 清黑名单文件
    └─ ProcessDirectoryTreePostOrder()         // 后序遍历扁平化
        ├─ 递归处理所有子目录（深度优先）
        └─ ProcessSingleDirectoryIfNeeded()    // 处理当前目录
            ├─ 检查是否符合扁平化条件
            │   ├─ subDirs.Length == 1
            │   └─ files.Length == 0
            ├─ DetermineFolderName()           // 决定最终名称
            └─ FlattenDirectory()              // 执行扁平化
                ├─ 移动子目录到临时位置（带GUID前缀）
                ├─ 删除空父目录
                └─ 重命名为最终名称
```

#### 3.6.3 关键实现

**批量处理入口：**

```csharp
private void CheckAndTriggerBatchSmartPathProcessing()
{
    if (!_settings.EnableSmartPathProcessing || _settings.OutputMode != OutputMode.ArchiveFolder)
        return;

    // 检查是否所有顶级文件项都已完成
    var topLevelItems = _fileList.Where(f => f.Parent == null).ToList();
    
    if (topLevelItems.Count == 0)
        return;

    bool allCompleted = topLevelItems.All(item => 
    {
        // 检查状态是否表示完成（扩大匹配范围）
        return item.Status.Contains("解压成功") || 
               item.Status.Contains("完成") ||
               item.Status.Contains("跳过") ||
               item.Status.Contains("非压缩文件") ||
               item.Status.Contains("失败") ||
               item.Status.Contains("异常") ||
               item.Status.Contains("取消");
    });

    if (allCompleted)
    {
        AppendLog($"[批量智能路径] 所有顶级文件项已完成，开始批量处理...", ConsoleColor.Cyan);
        
        // 收集所有解压生成的文件夹
        var extractedFolders = new HashSet<string>();
        foreach (var item in topLevelItems)
        {
            string extractedFolder = GetOutputDirectory(item.FilePath);
            
            if (!string.IsNullOrEmpty(extractedFolder) && Directory.Exists(extractedFolder))
            {
                extractedFolders.Add(extractedFolder);
            }
        }

        // 对每个解压生成的文件夹执行批量扁平化（fire-and-forget）
        foreach (var extractedFolder in extractedFolders)
        {
            _ = Task.Run(() => ProcessSmartPathBatch(extractedFolder));
        }
    }
}
```

**黑名单清理（扁平化前）：**

```csharp
private void DeleteBlacklistedFilesUnderDirectory(string rootDir)
{
    if (string.IsNullOrEmpty(rootDir) || !Directory.Exists(rootDir))
        return;

    var patterns = _settings.GetBlacklistPatterns();
    if (patterns == null || patterns.Count == 0)
        return;

    try
    {
        AppendLog($"[批量智能路径] 扁平化前黑名单清理: {rootDir}", ConsoleColor.Gray);
        foreach (string file in Directory.GetFiles(rootDir, "*.*", SearchOption.AllDirectories))
        {
            if (!File.Exists(file))
                continue;
            if (IsBlacklistedFile(file, out string matchedPattern))
                DeleteBlacklistedFile(file, matchedPattern);
        }
    }
    catch (Exception ex)
    {
        AppendLog($"[批量智能路径] 扁平化前黑名单扫描失败: {ex.Message}", ConsoleColor.Yellow);
    }
}
```

**后序遍历目录树：**

```csharp
private void ProcessDirectoryTreePostOrder(string dirPath)
{
    try
    {
        // 检查目录是否存在（可能在之前的扁平化操作中被删除）
        if (!Directory.Exists(dirPath))
            return;

        // 检查当前目录是否是压缩包解压出来的
        if (!IsExtractedArchiveFolder(dirPath))
        {
            AppendLog($"[批量智能路径] [DEBUG] {Path.GetFileName(dirPath)}: 非压缩包解压文件夹，跳过扁平化", ConsoleColor.Gray);
            return;
        }

        // 1. 先递归处理所有子目录（深度优先）
        var subDirs = Directory.GetDirectories(dirPath);
        foreach (var subDir in subDirs)
        {
            try
            {
                ProcessDirectoryTreePostOrder(subDir);
            }
            catch (Exception subEx)
            {
                // 单个子目录处理失败不影响其他子目录
                AppendLog($"[批量智能路径] 处理子目录失败 {subDir}: {subEx.Message}", ConsoleColor.Yellow);
            }
        }

        // 2. 处理当前目录（此时子目录已经处理完毕）
        if (Directory.Exists(dirPath))
        {
            ProcessSingleDirectoryIfNeeded(dirPath);
        }
    }
    catch (Exception ex)
    {
        AppendLog($"[批量智能路径] 遍历目录失败 {dirPath}: {ex.Message}", ConsoleColor.Red);
    }
}
```

**判断是否为压缩包解压文件夹：**

```csharp
private bool IsExtractedArchiveFolder(string dirPath)
{
    try
    {
        string dirName = Path.GetFileName(dirPath);
        string? parentDir = Path.GetDirectoryName(dirPath);
        
        if (string.IsNullOrEmpty(parentDir) || !Directory.Exists(parentDir))
            return false;

        var archiveExtensions = _settings.GetArchiveExtensions();
        
        // 方法1：检查目录名是否与某个压缩包文件名匹配（去掉扩展名后）
        var parentFiles = Directory.GetFiles(parentDir);
        foreach (var file in parentFiles)
        {
            string fileName = Path.GetFileNameWithoutExtension(file);
            
            // 检查是否是分卷压缩包
            bool isVolumeArchive = IsMultiVolumeArchive(file);
            
            // 获取压缩包的基础名称（去掉所有扩展名）
            string baseName = fileName;
            
            if (isVolumeArchive)
            {
                // 对于 .7z.001 这样的文件，fileName 是 "bb.7z"
                // 需要进一步去掉 .7z
                foreach (var ext in archiveExtensions.OrderByDescending(x => x.Length))
                {
                    if (baseName.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
                    {
                        baseName = baseName.Substring(0, baseName.Length - ext.Length);
                        break;
                    }
                }
            }
            
            // 检查目录名是否匹配
            if (dirName.Equals(baseName, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        
        // 方法2：如果目录下有压缩包，则认为这是压缩包解压出的文件夹
        var files = Directory.GetFiles(dirPath);
        foreach (var file in files)
        {
            if (IsArchiveFile(file))
                return true;
        }
        
        // 方法3：递归检查子目录
        var subDirs = Directory.GetDirectories(dirPath);
        foreach (var subDir in subDirs)
        {
            if (IsExtractedArchiveFolder(subDir))
                return true;
        }
        
        return false;
    }
    catch
    {
        // 如果检查失败，默认不进行扁平化（安全优先）
        return false;
    }
}
```

**决定最终文件夹名称：**

```csharp
private string DetermineFolderName(string parentName, string childName)
{
    string normalizedParentName = NormalizeArchiveLikeName(parentName);
    string normalizedChildName = NormalizeArchiveLikeName(childName);

    if (_settings.SmartPathProcessingMode == SmartPathMode.Concatenate)
    {
        // 拼接模式
        return $"{normalizedParentName}_{normalizedChildName}";
    }
    else
    {
        // 智能选择模式
        bool parentHasJapanese = System.Text.RegularExpressions.Regex.IsMatch(
            normalizedParentName, @"[\u3040-\u30FF\u3400-\u4DBF\u4E00-\u9FFF]");
        bool childHasJapanese = System.Text.RegularExpressions.Regex.IsMatch(
            normalizedChildName, @"[\u3040-\u30FF\u3400-\u4DBF\u4E00-\u9FFF]");

        bool parentIsLong = normalizedParentName.Length > 10;
        bool childIsLong = normalizedChildName.Length > 10;

        int parentScore = (parentHasJapanese ? 100 : 0) + (parentIsLong ? 50 : 0);
        int childScore = (childHasJapanese ? 100 : 0) + (childIsLong ? 50 : 0);

        // 选择评分高的，评分相同选较长的
        if (childScore > parentScore || (childScore == parentScore && normalizedChildName.Length > normalizedParentName.Length))
        {
            return normalizedChildName;
        }
        else
        {
            return normalizedParentName;
        }
    }
}
```

**执行扁平化：**

```csharp
private void FlattenDirectory(string parentDir, string childDir, string finalName)
{
    try
    {
        string? parentDirPath = Path.GetDirectoryName(parentDir);
        if (string.IsNullOrEmpty(parentDirPath))
            return;

        // 步骤1: 移动子目录到临时位置（避免冲突）
        string tempName = $"{Guid.NewGuid().ToString().Substring(0, 8)}_{finalName}";
        string tempPath = Path.Combine(parentDirPath, tempName);

        Directory.Move(childDir, tempPath);
        AppendLog($"[批量智能路径]   已移动: {Path.GetFileName(childDir)} -> {tempName}", ConsoleColor.Gray);

        // 步骤2: 删除空的父目录
        if (Directory.Exists(parentDir))
        {
            try
            {
                Directory.Delete(parentDir);
                AppendLog($"[批量智能路径]   已删除空目录: {Path.GetFileName(parentDir)}", ConsoleColor.Gray);
            }
            catch (Exception deleteEx)
            {
                AppendLog($"[批量智能路径] ⚠ 删除失败: {deleteEx.Message}", ConsoleColor.Yellow);
                return;
            }
        }

        // 步骤3: 重命名为最终名称
        string finalPath = Path.Combine(parentDirPath, finalName);
        
        if (Directory.Exists(finalPath))
        {
            Directory.Delete(finalPath, true);
            AppendLog($"[批量智能路径]   已删除已存在的目录: {finalName}", ConsoleColor.Gray);
        }

        Directory.Move(tempPath, finalPath);
        AppendLog($"[批量智能路径]   ✓ 扁平化完成: {finalName}", ConsoleColor.Green);
    }
    catch (Exception ex)
    {
        AppendLog($"[批量智能路径] 扁平化失败: {ex.Message}", ConsoleColor.Red);
    }
}
```

#### 3.6.4 关键特性

**后序遍历策略：**
- 从叶子节点开始处理，逐层向上
- 确保深层嵌套先被扁平化，浅层再处理
- 避免中间层目录已被删除导致的错误

**安全保护：**
- 每次操作前检查目录是否存在
- 使用 GUID 前缀避免文件名冲突
- 独立异常处理，单个目录失败不影响其他目录
- 只处理压缩包解压出的文件夹，不处理原始文件结构

**黑名单前置清理：**
- 在扁平化之前删除黑名单文件
- 避免同级黑名单文件导致「恰好一个子目录」条件不满足
- 确保扁平化能够正确触发

**智能名称选择：**
- **拼接模式**：父文件夹_子文件夹
- **智能选择模式**：根据日文和长度评分选择最优名称
  - 包含日文：+100分
  - 长度>10：+50分
  - 选择高分者，分数相同选较长者

---

## 4. 数据流转

### 4.1 文件生命周期

```
1. 拖拽文件
   ↓
2. 创建 FileItem，添加到 _fileList
   ↓
3. 加入 _pendingQueue
   ↓
4. 测试线程处理：
   - 判断是否压缩包
   - 测试密码
   - 记录到 _passwordMap
   ↓
5. 加入 _extractQueue
   ↓
6. 解压线程处理：
   - 获取密码
   - 执行解压
   - 扫描新文件
   ↓
7. 新文件加入 _pendingQueue（回到步骤4）
   ↓
8. 所有队列清空，处理完成
```

### 4.2 树形结构示例

```
Root
├─ file1.zip (顶级文件, Parent=null)
│  ├─ subfile1.rar (子压缩包, Parent=file1.zip)
│  │  └─ subsubfile1.7z (孙压缩包, Parent=subfile1.rar)
│  └─ subfile2.txt (普通文件, Parent=file1.zip)
└─ file2.7z (顶级文件, Parent=null)
   └─ subfile3.zip (子压缩包, Parent=file2.7z)
```

**父子关系规则：**
- 直接拖进来的文件：`Parent = null`
- 从压缩包解压出来的文件：`Parent = 父压缩包的 FileItem`
- 每个 `FileItem` 都有 `Children` 集合存储子项

### 4.3 密码管理流程

```
1. 启动程序
   ↓
2. 从 settings.json 加载 PermanentPasswords
   ↓
3. 按综合评分排序（使用次数 + 最近使用时间）
   ↓
4. 测试密码时：
   - 先测试 OneTimePasswords（临时密码）
   - 再测试 PermanentPasswords（永久密码）
   ↓
5. 找到密码后：
   - 记录到 _passwordMap
   - 增加使用次数
   - 更新最后使用时间
   ↓
6. 关闭程序时：
   - 保存 PermanentPasswords 到 settings.json
   - 清除 OneTimePasswords
```

### 4.4 状态流转

#### 文件状态流转

```
等待处理
  ↓ (测试线程)
正在测试密码
  ↓
测试: 无密码 / 测试: xxx
  ↓
密码正确: xxx / 无密码
  ↓ (加入待解压队列)
等待解压
  ↓ (解压线程)
正在解压...
  ↓
解压成功 (密码: xxx) / 解压成功 (无密码)
  ↓
扫描子文件 → 加入待处理队列
  ↓
等待子节点完成 (x/y)
  ↓
解压成功 (密码: xxx) [包含 y 个子压缩包]
```

#### 线程状态

```
_isTesting: true/false
_isExtracting: true/false

两个都为 false 且队列为空 → 处理完成
```

---

## 5. UI设计与状态管理

### 5.1 WPF 数据绑定

#### FileItem 实现 INotifyPropertyChanged

**问题：** 如果不实现该接口，属性变化不会通知 UI，导致状态不同步。

**解决方案：**

```csharp
public class FileItem : INotifyPropertyChanged
{
    private string _status = string.Empty;
    
    public event PropertyChangedEventHandler? PropertyChanged;
    
    protected virtual void OnPropertyChanged(string propertyName)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
    
    public string Status 
    { 
        get => _status; 
        set 
        { 
            _status = value; 
            OnPropertyChanged(nameof(Status));  // 通知UI
        } 
    }
    
    // 其他属性同理...
}
```

**工作流程：**

```
修复前（不工作）：
1. 子节点完成 → UpdateParentStatusWhenChildrenComplete()
2. parentItem.Status = "解压成功..."  ← 修改属性
3. ❌ UI没有收到通知，状态不变
4. 日志显示："已到达顶级，整个树形结构处理完成"
5. UI仍显示："等待处理"  ← 问题！

修复后（正常工作）：
1. 子节点完成 → UpdateParentStatusWhenChildrenComplete()
2. parentItem.Status = "解压成功..."  ← 修改属性
3. ✅ 触发 OnPropertyChanged("Status")
4. ✅ WPF数据绑定接收到通知
5. ✅ TreeView自动刷新显示
6. UI显示："解压成功 (密码: xxx) [包含 X 个子压缩包]"  ← 正确！
```

### 5.2 TreeView 层次化模板

```xml
<TreeView ItemsSource="{Binding FileList}">
    <TreeView.ItemTemplate>
        <HierarchicalDataTemplate ItemsSource="{Binding Children}">
            <StackPanel Orientation="Horizontal">
                <TextBlock Text="{Binding FileName}" />
                <TextBlock Text=" - " />
                <TextBlock Text="{Binding Status}" Foreground="{Binding StatusColor}" />
            </StackPanel>
        </HierarchicalDataTemplate>
    </TreeView.ItemTemplate>
</TreeView>
```

### 5.3 UI 线程交互规范

**重要原则：**

1. **避免 async void**：除事件处理器外，所有异步方法应返回 `Task`
2. **UI 集合保护**：所有对 `_fileList` 的读写必须通过 `Dispatcher.Invoke`
3. **调用链 Await 传递**：若底层方法改为 async Task，上游调用方必须添加 `await`

**示例：**

```csharp
// ✅ 正确：使用 Dispatcher.Invoke 保护 UI 集合
Dispatcher.Invoke(() =>
{
    parentFileItem.Children.Add(newFileItem);
});

// ❌ 错误：直接访问 UI 集合
parentFileItem.Children.Add(newFileItem);  // 可能跨线程异常
```

---

## 6. 密码管理系统

### 6.1 密码分类

| 类型 | 存储位置 | 生命周期 | 优先级 |
|------|---------|---------|--------|
| **一次性密码** | 内存（OneTimePasswords） | 本次运行有效 | 最高 |
| **永久密码** | settings.json | 持久化保存 | 按评分排序 |

### 6.2 密码排序算法

**综合评分公式：**

```csharp
public double Score
{
    get
    {
        double usageScore = UsageCount * 100;  // 使用次数权重
        
        // 时间衰减因子
        double timeScore = 0;
        var daysSinceLastUsed = (DateTime.Now - LastUsedTime).TotalDays;
        
        if (daysSinceLastUsed < 1)
            timeScore = 10000;  // 1小时内
        else if (daysSinceLastUsed < 7)
            timeScore = 5000;   // 7天内
        else if (daysSinceLastUsed < 30)
            timeScore = 1000;   // 30天内
        else
            timeScore = 100;    // 30天以上
        
        return usageScore + timeScore;
    }
}
```

**设计规范：**
- 时间窗口基础分值差异显著（10000 vs 5000 vs 1000）
- 使用次数权重总和不应超过最小时间窗口的基础分值
- 确保近期行为具有绝对优先级

### 6.3 密码本导入/导出

#### 导入功能

**操作步骤：**
1. 点击"浏览..."选择密码本文件（TAB分隔的txt格式）
2. 点击**"导入到配置"**按钮
3. 密码将从文本文件导入到 JSON 配置文件
4. 点击"保存配置"完成保存

**文件格式：**
```
密码1		使用次数		最后使用时间(Ticks)
密码2		使用次数		最后使用时间(Ticks)
```

#### 导出功能

**操作步骤：**
1. 点击**"导出密码本"**按钮
2. 选择保存路径
3. 所有永久密码将导出为 TAB 分隔的文本文件

### 6.4 密码测试日志

```
========== 开始测试: test.zip ==========
  ✗ 测试密码: 123456 -> 结果: 失败
  ✗ 测试密码: password -> 结果: 失败
  ✓ 测试密码: mypassword -> 结果: 成功
[test.zip] 密码测试成功，密码: mypassword
```

---

## 7. 关键算法与实现细节

### 7.1 递归与树形任务处理规范

#### 全量节点收集

**严禁仅遍历顶层根节点**，必须实现递归收集方法：

```csharp
private List<FileItem> CollectAllFileItems(ObservableCollection<FileItem> items)
{
    var result = new List<FileItem>();
    
    foreach (var item in items)
    {
        result.Add(item);
        
        // 递归收集子节点
        if (item.Children.Count > 0)
        {
            result.AddRange(CollectAllFileItems(item.Children));
        }
    }
    
    return result;
}
```

#### 统一递归机制

**避免单点终止**：父级任务完成不应直接终止工作流。

**流程连续性保证：**
1. 检测到新子任务（如解压出的新压缩包）
2. 显式触发新一轮处理流程
3. 任务调度基于"待处理队列"状态
4. 确保异步环境中子任务添加能正确唤醒新批次

#### 异步时序与完成判定

**避免过早判定**：父任务触发子任务后，严禁立即执行"父任务完成"检查。

**正确做法：**
- 依赖回调或全链路等待
- 父任务状态更新应依赖子任务完成后的回调
- 区分"添加"与"完成"：仅将子任务加入列表不代表处理完毕

### 7.2 去重与清理策略

#### 文件去重

```csharp
private bool CheckFileExistsInTree(string filePath, ObservableCollection<FileItem> items)
{
    foreach (var item in items)
    {
        if (item.FilePath == filePath)
            return true;
        
        // 递归检查子节点
        if (CheckFileExistsInTree(filePath, item.Children))
            return true;
    }
    
    return false;
}
```

#### 清理策略

- **顶级文件**（Parent == null）解压成功后从 `_fileList` 移除
- **子项**保留在父级 `Children` 中以维持结构
- 所有项完成后从 `_passwordMap` 中移除

### 7.3 并发控制

#### TryLock 机制

```csharp
public bool TryLock()
{
    lock (_lockObject)
    {
        if (_isLocked)
            return false;
        
        _isLocked = true;
        return true;
    }
}

public void ReleaseLock()
{
    lock (_lockObject)
    {
        _isLocked = false;
    }
}
```

**用途：** 防止多个线程同时处理同一文件

### 7.4 目录冲突检查

**问题：** 创建输出目录时，可能与同名文件冲突。

**解决方案：**

```csharp
if (File.Exists(outputDir))
{
    // 存在同名文件，切换到安全模式
    outputDir = Path.Combine(Path.GetDirectoryName(outputDir)!, 
                             $"{Path.GetFileNameWithoutExtension(outputDir)}_extracted");
    AppendLog($"检测到同名文件，切换到备用目录: {outputDir}", ConsoleColor.Yellow);
}

Directory.CreateDirectory(outputDir);
```

**特别关注：** 无扩展名文件极易与生成的同名文件夹发生冲突。

---

## 8. 配置管理

### 8.1 配置文件结构

**位置：** `%AppData%\AutoUnpackTool\settings.json`

```json
{
  "SevenZipPath": "C:\\Program Files\\7-Zip\\7z.exe",
  "PasswordFilePath": "E:\\passwords.txt",
  "OutputDir": "",
  "ThreadCount": 2,
  "FileAfterExtract": 0,
  "OutputMode": 2,
  "ExtractMode": 0,
  "OneTimePasswords": [],
  "PermanentPasswords": [
    {
      "Password": "password1",
      "UsageCount": 5,
      "LastUsedTime": "2026-04-05T23:00:00"
    },
    {
      "Password": "password2",
      "UsageCount": 3,
      "LastUsedTime": "2026-04-05T22:30:00"
    }
  ]
}
```

### 8.2 配置项说明

| 配置项 | 类型 | 说明 |
|--------|------|------|
| `SevenZipPath` | string | 7z.exe 路径 |
| `PasswordFilePath` | string | 原始密码本文件路径（仅记录） |
| `OutputDir` | string | 默认输出目录 |
| `ThreadCount` | int | 并发解压线程数（1-8） |
| `FileAfterExtract` | int | 解压后文件处理方式（0=保留, 1=删除, 2=回收站） |
| `OutputMode` | int | 输出模式（0=原目录, 1=指定目录, 2=以文件名命名） |
| `ExtractMode` | int | 解压模式（0=手动, 1=自动） |
| `OneTimePasswords` | array | 一次性密码列表 |
| `PermanentPasswords` | array | 永久密码列表（含使用统计） |

### 8.3 配置持久化修复

**历史问题：** `_permanentPasswords` 是私有字段，JSON 序列化时不会被保存。

**解决方案：** 改为公共属性

```csharp
// 修改前
private List<PasswordEntry> _permanentPasswords = new List<PasswordEntry>();

// 修改后
public List<PasswordEntry> PermanentPasswords { get; set; } = new List<PasswordEntry>();

// 保留向后兼容
private List<PasswordEntry> _permanentPasswords 
{ 
    get => PermanentPasswords; 
    set => PermanentPasswords = value; 
}
```

**加载逻辑优化：**

```csharp
public List<PasswordEntry> LoadPasswordsFromFile()
{
    // 优先从配置文件加载永久密码
    if (PermanentPasswords != null && PermanentPasswords.Count > 0)
    {
        return PermanentPasswords.OrderByDescending(p => p.Score).ToList();
    }

    // 如果配置文件没有密码，则从外部文件加载
    // ...
}
```

**导入后自动保存：**

```csharp
public void ImportPasswordsFromOldFormat(string oldPasswordFilePath)
{
    // ... 导入逻辑 ...
    
    if (importedCount > 0)
    {
        PermanentPasswords = PermanentPasswords.OrderByDescending(p => p.Score).ToList();
        
        // 自动保存到配置文件
        Save();
    }
}
```

---

## 9. 历史问题与解决方案

### 9.1 子压缩包停留在"等待处理"状态

**问题描述：** 日志显示"已到达顶级，整个树形结构处理完成"，但 UI 中顶级节点状态仍显示"等待处理"。

**根本原因：** 在 `CheckAndTriggerNextRoundExtractAsync()` 被调用时，`_isExtracting` 仍然是 `true`，导致条件判断失败。

**时序分析：**

```
时间线：
T1: 线程1 解压完成
T2: 调用 ScanExtractedFilesForArchives
T3: 添加子压缩包并测试密码
T4: Task.WhenAll 返回
T5: 调用 CheckAndTriggerNextRoundExtractAsync()
T6: 检查条件：pendingFiles.Count > 0 && !_isExtracting
    - pendingFiles.Count = 1 ✓
    - _isExtracting = true ❌ （还在 try 块内）
T7: 条件失败，不启动解压
T8: finally: _isExtracting = false
```

**解决方案：** 将 `CheckAndTriggerNextRoundExtractAsync()` 调用移到 `finally` 块之后

```csharp
finally
{
    _isExtracting = false;
    _extractCancellationTokenSource?.Dispose();
    _extractCancellationTokenSource = null;
    UpdateUiState();
}

// ⭐ 在 finally 之后调用，确保 _isExtracting 已经是 false
await CheckAndTriggerNextRoundExtractAsync();
```

### 9.2 UI 状态不更新

**问题描述：** 属性值已修改，但 UI 没有反映变化。

**根本原因：** `FileItem` 类没有实现 `INotifyPropertyChanged` 接口。

**解决方案：** 让所有属性实现通知机制（见 5.1 节）

### 9.3 密码配置文件未保存

**问题描述：** 导入密码后，settings.json 中没有 `PermanentPasswords` 数组。

**根本原因：** 私有字段无法被 JSON 序列化。

**解决方案：** 改为公共属性，并在导入后自动调用 `Save()`（见 8.3 节）

### 9.4 线程过早退出

**问题描述：** 队列为空时线程直接退出，新加入的文件无法被处理。

**解决方案：** 使用 `ManualResetEventSlim` 实现信号量唤醒机制（见 3.2 节）

---

## 10. 开发规范与最佳实践

### 10.1 C# 异步编程规范

#### 避免 async void

```csharp
// ❌ 错误：async void（除事件处理器外）
private async void ProcessFile() { ... }

// ✅ 正确：返回 Task
private async Task ProcessFile() { ... }
```

#### Dispatcher 使用规范

```csharp
// ✅ 正确：不 await Dispatcher.InvokeAsync 时赋值给 _
_ = Dispatcher.InvokeAsync(async () => {
    await SomeAsyncOperation();
});

// ✅ 正确：需要等待时使用 Invoke
await Dispatcher.Invoke(async () => {
    await SomeAsyncOperation();
});
```

#### 调用链 Await 传递

```csharp
// 底层方法
private async Task<bool> TestPasswordAsync(...) { ... }

// 上游调用方必须 await
private async Task ProcessFile()
{
    bool result = await TestPasswordAsync(...);  // ✅ 必须 await
}
```

### 10.2 线程安全规范

#### UI 集合保护

```csharp
// ✅ 正确：通过 Dispatcher 保护
Dispatcher.Invoke(() =>
{
    _fileList.Add(newItem);
});

// ❌ 错误：直接访问
_fileList.Add(newItem);  // 可能跨线程异常
```

#### 并发队列使用

```csharp
// ✅ 使用 ConcurrentQueue
private ConcurrentQueue<FileItem> _pendingQueue = new();

// 入队
_pendingQueue.Enqueue(item);

// 出队
if (_pendingQueue.TryDequeue(out var item))
{
    // 处理 item
}
```

### 10.3 代码组织规范

#### 方法职责单一

```csharp
// ✅ 好：每个方法只做一件事
private async Task ProcessPendingFile(...) { ... }
private async Task ProcessExtractFile(...) { ... }
private void ScanExtractedFilesForArchives(...) { ... }

// ❌ 坏：一个方法做太多事
private async Task ProcessEverything(...) { ... }
```

#### 日志记录规范

```csharp
// ✅ 详细日志，便于调试
AppendLog($"[DEBUG] _pendingQueue.Count: {_pendingQueue.Count}", ConsoleColor.Magenta);
AppendLog($"[测试线程] 队列为空，等待新任务...", ConsoleColor.Gray);

// ❌ 缺少上下文
AppendLog("Error occurred");
```

### 10.4 错误处理规范

#### 异常捕获与上报

```csharp
try
{
    await ProcessFile(fileItem);
}
catch (OperationCanceledException)
{
    fileItem.Status = "已取消";
    if (fileItem.Parent != null)
        UpdateParentStatusWhenChildrenComplete(fileItem);
    throw;
}
catch (Exception ex)
{
    fileItem.Status = $"异常: {ex.Message}";
    if (fileItem.Parent != null)
        UpdateParentStatusWhenChildrenComplete(fileItem);
    AppendLog($"处理失败: {ex.Message}", ConsoleColor.Red);
}
```

### 10.5 性能优化建议

#### 避免频繁 UI 更新

```csharp
// ❌ 错误：每次循环都更新 UI
foreach (var item in items)
{
    item.Status = "处理中";  // 触发 UI 更新
}

// ✅ 正确：批量更新
Dispatcher.Invoke(() =>
{
    foreach (var item in items)
    {
        item.Status = "处理中";
    }
});
```

#### 合理使用异步

```csharp
// ✅ 耗时操作使用异步
await Task.Run(() => HeavyComputation());

// ❌ 轻量操作无需异步
await Task.Run(() => x + y);  // 过度使用
```

---

## 附录

### A. 关键文件清单

| 文件 | 说明 |
|------|------|
| `MainWindow.xaml.cs` | 主窗口逻辑，包含双队列架构、状态机等核心实现 |
| `AppSettings.cs` | 配置管理类，包含密码管理、设置持久化 |
| `SevenZipExtractor.cs` | 7-Zip 封装，提供异步测试和解压方法 |
| `PasswordManagerDialog.xaml.cs` | 密码管理器对话框 |
| `SettingsDialog.xaml.cs` | 设置对话框 |

### B. 常用命令

```bash
# 恢复依赖
dotnet restore

# 编译项目
dotnet build

# 运行程序
dotnet run

# 发布Release版本
dotnet publish -c Release -r win-x64 --self-contained false
```

### C. 参考资料

- [WPF 数据绑定官方文档](https://docs.microsoft.com/zh-cn/dotnet/desktop/wpf/data/)
- [.NET 异步编程最佳实践](https://docs.microsoft.com/zh-cn/dotnet/csharp/asynchronous-programming/)
- [Concurrent Collections](https://docs.microsoft.com/zh-cn/dotnet/standard/collections/thread-safe/)

---

**文档版本：** v1.0  
**最后更新：** 2026-04-11  
**维护者：** AutoUnpackTool 开发团队
