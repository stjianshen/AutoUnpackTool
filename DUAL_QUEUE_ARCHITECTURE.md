# 双队列架构设计文档

## 概述

AutoUnpackTool 采用**双队列架构**实现压缩包的递归解压，将密码测试和解压流程分离为两个独立的线程和队列，实现清晰的职责分工和高效的并发处理。

## 核心架构

### 数据结构

```csharp
// 两个并发队列
private ConcurrentQueue<FileItem> _pendingQueue = new();     // 待处理队列（测试密码）
private ConcurrentQueue<FileItem> _extractQueue = new();     // 待解压队列（已测试密码）

// 队列状态监控 - 用于唤醒休眠的线程
private ManualResetEventSlim _pendingQueueSignal = new(false);   // 待处理队列有数据信号
private ManualResetEventSlim _extractQueueSignal = new(false);   // 待解压队列有数据信号

// 状态标识
private bool _isTesting = false;      // 测试线程是否运行
private bool _isExtracting = false;   // 解压线程是否运行

// 任务完成信号
private TaskCompletionSource<bool> _testCompletionSource = new();
private TaskCompletionSource<bool> _extractCompletionSource = new();
```

### 信号量唤醒机制

**问题：** 如果队列为空时线程退出，新加入的文件将无法被处理。

**解决方案：** 使用 `ManualResetEventSlim` 实现线程休眠和唤醒。

```csharp
// 1. 线程等待信号（而不是直接退出）
while (!token.IsCancellationRequested)
{
    if (queue.TryDequeue(out var item))
    {
        // 处理文件...
    }
    else
    {
        // 队列为空，等待信号（最多30秒）
        bool signaled = await Task.Run(() => 
            _queueSignal.Wait(TimeSpan.FromSeconds(30), token), token);
        
        if (!signaled && queue.IsEmpty)
        {
            break;  // 超时且确实为空，才退出
        }
        
        _queueSignal.Reset();  // 重置信号
    }
}

// 2. 入队时唤醒线程
queue.Enqueue(item);
_queueSignal.Set();  // 设置信号，唤醒等待的线程
```

**优势：**
- ✅ 线程不会过早退出
- ✅ 新文件加入时立即唤醒
- ✅ 超时机制防止永久阻塞
- ✅ 支持取消操作

### 流程图

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
│    解压文件                                           │
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

## 核心方法

### 1. StartDualQueueProcessing()

启动双队列处理流程的入口方法。

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

### 2. StartTestThread()

测试线程：从待处理队列取文件，测试密码后放入待解压队列。

**职责：**
- 从 `_pendingQueue` 取出文件
- 判断是否是压缩文件
  - 如果不是：标记为"非压缩文件，跳过"，检查父项完成
  - 如果是：继续测试密码
- 测试密码（先测试无密码，再测试密码本）
- 记录密码到 `_passwordMap`
- 更新文件状态
- 将文件加入 `_extractQueue`

**关键特性：**
- 单线程顺序处理
- 循环等待新文件（队列为空时不立即退出）
- 支持取消操作

### 3. StartExtractThread()

解压线程：从待解压队列取文件，解压后扫描新文件加入待处理队列。

**职责：**
- 从 `_extractQueue` 取出文件
- 获取密码（从 `_passwordMap`）
- 执行解压操作
- 扫描输出目录，发现新的压缩文件
- 将新文件添加到树形结构并加入 `_pendingQueue`
- 检查父项完成

**关键特性：**
- 多线程并发处理（根据 `ThreadCount` 设置）
- 使用 `TryLock()` 防止重复处理
- **循环等待新文件（使用信号量机制）**
- **队列为空时休眠，不退出**

### 4. WakeupTestThread() / WakeupExtractThread()

唤醒方法：当有新文件加入队列时，唤醒休眠的线程。

```csharp
private void WakeupTestThread()
{
    _pendingQueueSignal.Set();  // 设置信号
    AppendLog("[系统] 已唤醒测试线程", ConsoleColor.Gray);
}

private void WakeupExtractThread()
{
    _extractQueueSignal.Set();  // 设置信号
    AppendLog("[系统] 已唤醒解压线程", ConsoleColor.Gray);
}
```

**调用时机：**
1. 拖拽文件入队后 → `WakeupTestThread()`
2. 测试完成加入待解压队列 → `WakeupExtractThread()`
3. 解压后发现新文件加入待处理队列 → `WakeupTestThread()`

### 4. ProcessPendingFile()

处理单个待处理文件的核心逻辑。

```csharp
private async Task ProcessPendingFile(FileItem fileItem, SevenZipExtractor extractor, 
                                     List<string> passwords, CancellationToken token)
{
    // 步骤1：判断是否是压缩文件
    if (!IsArchiveFile(fileItem.FilePath))
    {
        fileItem.Status = "非压缩文件，跳过";
        if (fileItem.Parent != null)
            UpdateParentStatusWhenChildrenComplete(fileItem.Parent);
        return;
    }

    // 步骤2：测试密码
    string? foundPassword = null;
    
    // 2.1 尝试无密码
    bool noPasswordSuccess = await extractor.TestPasswordAsync(fileItem.FilePath, "", cancellationToken: token);
    if (noPasswordSuccess)
    {
        foundPassword = null;
    }
    else
    {
        // 2.2 测试密码本
        foreach (var password in passwords)
        {
            bool isValid = await extractor.TestPasswordAsync(fileItem.FilePath, password, cancellationToken: token);
            if (isValid)
            {
                foundPassword = password;
                _settings.RecordPasswordUsage(password);
                break;
            }
        }
    }

    // 步骤3：记录密码
    _passwordMap.Add(fileItem.FilePath, foundPassword);
    fileItem.FoundPassword = foundPassword;
    fileItem.Status = foundPassword != null ? $"密码正确: {foundPassword}" : "无密码";

    // 步骤4：加入待解压队列
    _extractQueue.Enqueue(fileItem);
}
```

### 5. ScanExtractedFilesForArchives()

扫描解压后的文件，检测新的压缩文件并添加到待处理队列。

**职责：**
- 扫描输出目录
- 识别压缩文件（基于扩展名和文件头）
- 创建 `FileItem` 对象
- 添加到父项的 `Children` 集合
- 将新文件加入 `_pendingQueue`
- **调用 `WakeupTestThread()` 唤醒测试线程**

**注意：**
- 此方法只负责扫描和添加文件
- 不再测试密码或启动解压
- 由测试线程统一处理

**职责：**
- 扫描输出目录
- 识别压缩文件（基于扩展名和文件头）
- 创建 `FileItem` 对象
- 添加到父项的 `Children` 集合
- 将新文件加入 `_pendingQueue`

**注意：**
- 此方法只负责扫描和添加文件
- 不再测试密码或启动解压
- 由测试线程统一处理

## 数据流转

### 文件生命周期

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

### 树形结构

```
Root
├─ file1.zip (顶级文件)
│  ├─ subfile1.rar (子压缩包)
│  │  └─ subsubfile1.7z (孙压缩包)
│  └─ subfile2.txt (普通文件)
└─ file2.7z (顶级文件)
   └─ subfile3.zip (子压缩包)
```

**父子关系：**
- 直接拖进来的文件：`Parent = null`
- 从压缩包解压出来的文件：`Parent = 父压缩包的 FileItem`
- 每个 `FileItem` 都有 `Children` 集合存储子项

## 优势

### 1. 职责清晰
- **测试线程**：专门负责密码测试
- **解压线程**：专门负责解压操作
- 单一职责原则，易于维护和调试

### 2. 解耦性好
- 两个队列独立运作
- 测试和解压互不干扰
- 可以单独启动测试或解压

### 3. 扩展性强
- 可以轻松添加新的处理阶段
- 只需在队列之间插入新的处理逻辑
- 支持动态调整线程数

### 4. 并发安全
- 使用 `ConcurrentQueue` 保证线程安全
- 使用 `TryLock()` 防止重复处理
- 使用 `TaskCompletionSource` 协调完成状态

### 5. 资源利用率高
- 测试和解压可以并行进行
- 多线程并发解压
- 自动负载均衡

## 状态管理

### 文件状态流转

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
```

### 线程状态

```
_isTesting: true/false
_isExtracting: true/false

两个都为 false 且队列为空 → 处理完成
```

## 调用时机

### 1. 拖拽文件
```csharp
// 文件添加到列表后
foreach (var file in files)
{
    var fileItem = _fileList.FirstOrDefault(f => f.FilePath == file);
    if (fileItem != null)
        _pendingQueue.Enqueue(fileItem);
}

StartDualQueueProcessing();
```

### 2. 手动点击按钮
```csharp
// 密码测试按钮
foreach (var fileItem in _fileList)
    _pendingQueue.Enqueue(fileItem);

StartDualQueueProcessing();

// 解压按钮
StartDualQueueProcessing();
```

### 3. 自动模式
```csharp
// CheckAndTriggerAutoExtract()
if (_settings.ExtractMode == ExtractMode.Auto)
    StartDualQueueProcessing();
```

## 注意事项

### 1. 信号量唤醒机制（重要！）

**问题：** 如果线程在队列为空时直接退出，新加入的文件将无法被处理。

**解决方案：** 使用 `ManualResetEventSlim` 实现线程休眠和唤醒。

```csharp
// 线程等待逻辑
while (!token.IsCancellationRequested)
{
    if (queue.TryDequeue(out var item))
    {
        // 处理文件...
    }
    else
    {
        // 队列为空，等待信号（最多30秒）
        AppendLog("队列为空，等待新任务...", ConsoleColor.Gray);
        
        bool signaled = await Task.Run(() => 
            _queueSignal.Wait(TimeSpan.FromSeconds(30), token), token);
        
        if (!signaled && queue.IsEmpty)
        {
            break;  // 超时且确实为空，才退出
        }
        
        _queueSignal.Reset();  // 重置信号
    }
}

// 入队时唤醒
queue.Enqueue(item);
_queueSignal.Set();  // 立即唤醒等待的线程
```

**优势：**
- ✅ 线程不会过早退出
- ✅ 新文件加入时立即唤醒（响应时间 < 1ms）
- ✅ 超时机制防止永久阻塞（30秒）
- ✅ 支持取消操作

### 2. 调用唤醒方法的时机

必须在以下位置调用唤醒方法：

```csharp
// 1. 拖拽文件后
_pendingQueue.Enqueue(fileItem);
WakeupTestThread();

// 2. 测试完成后加入待解压队列
_extractQueue.Enqueue(fileItem);
WakeupExtractThread();

// 3. 解压后发现新文件
_pendingQueue.Enqueue(newFile);
WakeupTestThread();
```

**忘记调用的后果：**
- ❌ 线程处于休眠状态
- ❌ 新文件不会被处理
- ❌ 需要等待30秒超时或手动重启

### 3. 队列为空时的处理

两个线程都不会因为队列为空而立即退出，而是会等待信号或超时，确保不会错过新加入的文件。

```csharp
if (_pendingQueue.IsEmpty && _extractQueue.IsEmpty)
{
    // 等待信号（最多30秒）
    bool signaled = await Task.Run(() => 
        _queueSignal.Wait(TimeSpan.FromSeconds(30), token), token);
    
    if (!signaled && _pendingQueue.IsEmpty && _extractQueue.IsEmpty)
        break;  // 确认没有新文件，退出循环
}
```

**与旧方案对比：**

| 特性 | 旧方案 (Task.Delay) | 新方案 (ManualResetEventSlim) |
|------|-------------------|----------------------------|
| 响应速度 | 慢 (100-500ms) | 快 (< 1ms) |
| CPU占用 | 高 (频繁轮询) | 低 (阻塞等待) |
| 可靠性 | 低 (可能错过文件) | 高 (立即唤醒) |
| 资源消耗 | 高 | 低 |

### 4. 完成信号
使用 `TaskCompletionSource` 通知监控方法两个线程都已完成。

```csharp
finally
{
    _isTesting = false;
    _testCompletionSource.TrySetResult(true);
}
```

### 5. 父项完成检查
当子项处理完成后，需要检查父项是否可以标记为完成。

```csharp
if (fileItem.Parent != null)
{
    UpdateParentStatusWhenChildrenComplete(fileItem.Parent);
}
```

## 总结

双队列架构通过分离密码测试和解压流程，实现了：
- ✅ 清晰的职责分工
- ✅ 高效的并发处理
- ✅ 灵活的扩展能力
- ✅ 可靠的线程安全
- ✅ 优雅的递归处理

这种设计使得子压缩包的递归解压变得简单可靠，完全复用了父压缩包的处理流程。
