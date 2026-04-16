# 智能路径处理 Bug 修复

## 问题描述

在智能路径处理过程中，当移动子文件夹后尝试删除原目录时出现异常：

```
[智能路径] 处理失败: Could not find a part of the path 'D:\tem\test\1动n\1动n\5元包月加速器推荐 安卓 PC 苹果通用 已更新最新网址。'.
```

## 根本原因

原代码在删除空目录时调用了 `CleanEmptyDirectories(extractedFolder)`，这个方法会递归地清理所有空子目录。但问题是：

1. 我们已经将唯一的子文件夹移动到上级目录
2. `CleanEmptyDirectories` 会递归检查并删除空目录
3. 在某些情况下，它可能会先删除了 `extractedFolder` 本身
4. 然后代码又尝试访问已被删除的目录，导致 "Could not find a part of the path" 异常

## 修复方案

简化删除逻辑，直接删除 `extractedFolder`，因为我们已经确认：
- 该目录下只有一个子文件夹（已经被移走）
- 该目录下没有文件
- 所以它现在是空的，可以直接删除

### 修复前代码

```csharp
if (Directory.Exists(extractedFolder))
{
    // 先清理可能存在的空子目录
    CleanEmptyDirectories(extractedFolder);
    
    // 如果文件夹仍然为空，则删除它
    if (Directory.GetFiles(extractedFolder).Length == 0 && Directory.GetDirectories(extractedFolder).Length == 0)
    {
        Directory.Delete(extractedFolder);
        AppendLog($"[智能路径]   已删除: {Path.GetFileName(extractedFolder)}", ConsoleColor.Gray);
    }
}
```

### 修复后代码

```csharp
// 由于我们已经将唯一的子文件夹移走了，extractedFolder 应该是空的
// 直接尝试删除它
if (Directory.Exists(extractedFolder))
{
    try
    {
        Directory.Delete(extractedFolder);
        AppendLog($"[智能路径]   已删除: {Path.GetFileName(extractedFolder)}", ConsoleColor.Gray);
    }
    catch (Exception deleteEx)
    {
        AppendLog($"[智能路径] ⚠ 删除空文件夹失败: {deleteEx.Message}", ConsoleColor.Yellow);
        AppendLog($"[智能路径]   保留原文件夹: {Path.GetFileName(extractedFolder)}", ConsoleColor.Yellow);
    }
}
```

## 改进点

1. **简化逻辑**：不再需要递归清理，因为我们知道目录结构
2. **更好的错误处理**：如果删除失败，记录错误但继续执行后续步骤
3. **避免竞态条件**：不会在删除过程中访问已被删除的目录

## 测试建议

请重新测试之前的场景，特别是：
1. 单层嵌套文件夹
2. 包含日文的文件夹名
3. 长名称文件夹

预期结果应该是：
- 子文件夹成功移动到上级目录
- 原空文件夹被成功删除
- 临时文件夹成功重命名为最终名称
- 没有异常发生

