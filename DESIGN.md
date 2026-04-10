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

⏳ 分卷压缩解压支持  
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

### 3.1 双队列架构

#### 设计理念

将密码测试和解压流程分离为两个独立的线程和队列，实现：
- ✅ 清晰的职责分工
- ✅ 高效的并发处理
- ✅ 灵活的扩展能力

#### 工作流程图

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
│    测试密码 → 找到密码                                │
│    file.FoundPassword = password                     │
│    file.Status = "密码正确/无密码"                    │
│    extractQueue.Enqueue(file)  // 加入待解压队列      │
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
│                                                       │
│  while (有文件) {                                    │
│    file = extractQueue.TryDequeue()                  │
│                                                       │
│    获取密码 → 执行解压                                │
│                                                       │
│    扫描输出目录 → 发现新文件                           │
│    foreach (新文件) {                                │
│      创建 FileItem                                   │
│      pendingQueue.Enqueue(新文件)  // 加入待处理队列  │
│    }                                                  │
│                                                       │
│    检查父项完成                                       │
│  }                                                    │
└──────┬──────────────────────────────────────────────┘
       │
       ▼
┌─────────────────────────────────────────────────────┐
│         监控完成 (MonitorProcessingComplete)          │
│                                                       │
│  await Task.WhenAll(                                 │
│    _testCompletionSource.Task,                       │
│    _extractCompletionSource.Task                     │
│  )                                                    │
│                                                       │
│  AppendLog("所有处理流程完成")                        │
└─────────────────────────────────────────────────────┘
```

#### 核心方法

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

### 3.3 反向传播机制（父子状态同步）

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
