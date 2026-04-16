# 密码测试日志增强与队列修复

## 修改日期
2026-04-16

## 问题描述

### 1. 密码测试没有执行
从日志可以看到，测试线程启动后直接显示"队列为空，等待新任务..."，文件加入队列后没有触发密码测试流程。

**原因分析：**
- `Window_Drop`方法中，代码会将所有状态为"等待处理"的文件都加入队列
- 这会导致重复添加已存在的文件到队列
- 队列逻辑混乱，测试线程无法正确获取新任务

### 2. NanaZip权限问题
```
[线程 2] 2.zip: 解压失败 - 解压异常: An error occurred trying to start process 
'C:\Program Files\WindowsApps\40174MouriNaruto.NanaZip_6.0.1650.0_x64__gnj4mf6z9tkrc\NanaZip.Universal.Console.exe' 
with working directory 'E:\learn\AutoUnpackTool'. 拒绝访问。
```

**原因分析：**
- NanaZip是UWP应用，安装在WindowsApps目录下
- UWP应用有沙箱限制，普通进程可能无法直接调用
- 建议使用标准版7-Zip

### 3. 缺少详细的密码测试日志
用户需要看到每个密码的测试过程和结果，包括：
- 当前测试的是第几个密码
- 7z命令的输出信息
- 密码测试的成功/失败状态

## 解决方案

### 1. 修复队列重复添加问题

#### 修改文件：MainWindow.xaml.cs

**修改点1：Window_Drop方法**
```csharp
// 之前：将所有"等待处理"的文件加入队列（会重复）
foreach (var fileItem in _fileList.Where(f => f.Status == "等待处理").ToList())
{
    _pendingQueue.Enqueue(fileItem);
}

// 现在：只添加新创建的文件
var newFileItems = new List<FileItem>();  // 记录新添加的文件
// ... 在处理文件时收集新文件 ...
foreach (var fileItem in newFileItems)
{
    _pendingQueue.Enqueue(fileItem);
    AppendLog($"  [队列] {fileItem.FileName} 已加入待测试队列", ConsoleColor.Gray);
}
```

**修改点2：相关方法签名**
- `AddFileToList(string filePath, ref int addedCount, List<FileItem>? newFileItems = null)`
- `ProcessSingleFile(string filePath, ref int addedCount, List<FileItem>? newFileItems = null)`
- `ScanDirectoryForArchives(string directoryPath, ref int addedCount, List<FileItem>? newFileItems = null)`

所有添加文件的方法都增加`newFileItems`参数，用于跟踪新创建的文件项。

### 2. 增强密码测试日志

#### 修改文件：MainWindow.xaml.cs - ProcessPendingFile方法

**新增日志输出：**

1. **无密码测试阶段**
```csharp
AppendLog($"  [密码测试] 开始测试无密码...", ConsoleColor.Gray);
bool noPasswordSuccess = await extractor.TestPasswordAsync(
    fileItem.FilePath, 
    "", 
    onOutput: (msg) => AppendLog($"    [7z输出] {msg}", ConsoleColor.DarkGray),
    cancellationToken: token);

if (noPasswordSuccess)
{
    foundPassword = null;
    AppendLog($"  [密码测试] ✓ 无密码成功", ConsoleColor.Green);
}
else
{
    AppendLog($"  [密码测试] ✗ 无密码失败，开始测试密码本 ({passwords.Count}个密码)...", ConsoleColor.Yellow);
}
```

2. **密码本测试阶段**
```csharp
int testedCount = 0;
foreach (var password in passwords)
{
    testedCount++;
    
    // 显示进度
    if (testedCount <= 5 || testedCount % 10 == 0)
    {
        AppendLog($"  [密码测试] [{testedCount}/{passwords.Count}] 测试: {password}", ConsoleColor.Gray);
    }
    
    bool isValid = await extractor.TestPasswordAsync(
        fileItem.FilePath, 
        password,
        onOutput: (msg) => 
        {
            if (msg.Contains("Wrong password") || msg.Contains("错误") || msg.Contains("Error"))
            {
                AppendLog($"    [7z错误] {msg}", ConsoleColor.DarkRed);
            }
        },
        cancellationToken: token);
    
    if (isValid)
    {
        foundPassword = password;
        _settings.RecordPasswordUsage(password);
        AppendLog($"  [密码测试] ✓ 找到正确密码: {password} (第{testedCount}个)", ConsoleColor.Green);
        break;
    }
}

if (foundPassword == null)
{
    AppendLog($"  [密码测试] ✗ 已测试完所有{passwords.Count}个密码，均未成功", ConsoleColor.Red);
}
```

3. **UI状态更新**
```csharp
fileItem.Status = $"测试: {password} ({testedCount}/{passwords.Count})";
```

### 3. NanaZip权限警告

#### 修改文件：MainWindow.xaml.cs - Window_Loaded方法

```csharp
if (!string.IsNullOrEmpty(_settings.SevenZipPath))
{
    AppendLog($"已加载配置: 7z.exe = {_settings.SevenZipPath}", ConsoleColor.Cyan);
    
    // 检查是否是 NanaZip 的 WindowsApps 路径
    if (_settings.SevenZipPath.Contains("WindowsApps") || _settings.SevenZipPath.Contains("NanaZip"))
    {
        AppendLog("⚠️  警告: 检测到 NanaZip (UWP版本)", ConsoleColor.Yellow);
        AppendLog("   UWP应用可能有权限限制，建议使用标准 7-Zip", ConsoleColor.Yellow);
        AppendLog("   下载地址: https://www.7-zip.org/", ConsoleColor.Gray);
    }
}
```

## 预期效果

### 修复后的日志示例

```
[22:56:16]--> 已添加 1 个压缩文件到列表
[22:56:16]-->   [队列] 2.zip 已加入待测试队列
[22:56:16]--> 自动开始处理流程...
[22:56:16]--> 
========== 测试线程启动 ==========
[22:56:16]--> 加载密码本: 共 96 个密码
[22:56:16]--> 
[测试] 2.zip
[22:56:16]-->   [密码测试] 开始测试无密码...
[22:56:16]-->     [7z输出] Listing archive: 2.zip
[22:56:16]-->     [7z输出] Path = 2.zip
[22:56:16]-->     [7z输出] Type = zip
[22:56:16]-->   [密码测试] ✗ 无密码失败，开始测试密码本 (96个密码)...
[22:56:16]-->   [密码测试] [1/96] 测试: 123456
[22:56:17]-->     [7z错误] Wrong password
[22:56:17]-->   [密码测试] [2/96] 测试: password
[22:56:17]-->   [密码测试] [3/96] 测试: admin
[22:56:18]-->   [密码测试] ✓ 找到正确密码: admin (第3个)
[22:56:18]--> [2.zip] 已加入待解压队列
```

### 关键改进

1. ✅ **队列不再重复添加** - 只添加新创建的文件项
2. ✅ **详细的密码测试日志** - 显示每个密码的测试进度和结果
3. ✅ **7z输出捕获** - 显示关键的错误信息
4. ✅ **NanaZip警告** - 提示用户使用标准7-Zip避免权限问题
5. ✅ **测试计数显示** - UI上显示"测试: xxx (3/96)"

## 注意事项

### NanaZip权限问题解决方案

如果继续使用NanaZip遇到"拒绝访问"错误，有以下解决方案：

1. **推荐方案：使用标准7-Zip**
   - 下载：https://www.7-zip.org/
   - 安装后在软件设置中配置路径，例如：`C:\Program Files\7-Zip\7z.exe`

2. **备选方案：以管理员身份运行**
   - 右键点击AutoUnpackTool.exe
   - 选择"以管理员身份运行"
   - 这可能解决部分权限问题

3. **备选方案：使用7zG.exe**
   - NanaZip的GUI版本可能有不同的权限模型
   - 尝试配置为：`...\NanaZipG.exe`

## 测试建议

1. 关闭当前运行的程序
2. 重新编译并运行
3. 拖拽一个已知密码的压缩包进行测试
4. 观察日志输出是否包含详细的密码测试信息
5. 验证队列不会重复添加文件

## 相关文件

- MainWindow.xaml.cs - 主要修改文件
- SevenZipExtractor.cs - 密码测试逻辑（已有onOutput回调支持）
