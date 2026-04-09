# 密码配置文件保存修复说明

## 问题描述

用户反馈导入密码后，配置文件（settings.json）中没有保存密码部分，导出密码本也是空的。

## 问题原因

1. **私有字段无法序列化**：`_permanentPasswords` 是私有字段，JSON序列化时不会被保存到配置文件
2. **导入后未自动保存**：导入密码后只更新了内存中的数据，没有调用保存方法
3. **加载逻辑问题**：每次加载密码都从外部文件读取，没有优先使用配置文件中已保存的密码

## 修复方案

### 1. 将私有字段改为公共属性

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

### 2. 修改加载密码逻辑

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

### 3. 导入后自动保存

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

### 4. 更新所有引用

将所有使用 `_permanentPasswords` 的地方改为使用 `PermanentPasswords`：
- `GetAllPasswords()`
- `AddPermanentPassword()`
- `RemovePermanentPassword()`
- `RecordPasswordUsage()`
- `SavePasswordsToFile()`

## 配置文件格式

修复后，配置文件将包含永久密码列表：

```json
{
  "SevenZipPath": "C:\\path\\to\\7z.exe",
  "PasswordFilePath": "E:\\path\\to\\passwords.txt",
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

## 测试步骤

### 测试1：导入密码本
1. 打开软件设置
2. 点击"浏览..."选择密码本文件（TAB分隔的txt文件）
3. 点击"导入到配置"按钮
4. 检查配置文件（settings.json）中是否包含 `PermanentPasswords` 数组
5. 验证密码已正确保存

### 测试2：导出密码本
1. 打开软件设置
2. 点击"导出密码本"按钮
3. 选择保存路径
4. 打开导出的txt文件，验证内容格式正确：
   ```
   password1		5		639111234567890123
   password2		3		639111234567890123
   ```

### 测试3：密码持久化
1. 导入密码本后关闭程序
2. 重新打开程序
3. 打开密码管理器，验证密码列表仍然存在
4. 拖拽压缩包测试，验证密码能正确加载

### 测试4：密码使用统计
1. 测试密码成功后，检查使用次数是否增加
2. 关闭并重新打开程序
3. 验证使用次数已持久化保存
4. 验证密码按综合评分（使用次数+最近使用时间）排序

## 注意事项

1. **首次导入**：首次使用导入功能时，会覆盖配置文件中现有的密码
2. **密码去重**：导入时自动去除重复密码
3. **自动排序**：密码按综合评分自动排序，确保常用密码优先测试
4. **向后兼容**：保留了 `_permanentPasswords` 私有字段作为兼容层，不影响现有代码

## 修改文件清单

1. **AppSettings.cs**
   - 将 `_permanentPasswords` 改为 `PermanentPasswords` 公共属性
   - 修改 `LoadPasswordsFromFile()` 优先从配置文件加载
   - 修改 `ImportPasswordsFromOldFormat()` 自动保存
   - 更新所有引用 `_permanentPasswords` 的方法

2. **SettingsDialog.xaml.cs**
   - 导入和导出功能无需修改（已正确使用API）

3. **PasswordManagerDialog.xaml.cs**
   - 无需修改（已通过 `LoadPasswordsFromFile()` 间接使用）

## 预期效果

- ✅ 导入密码后，配置文件包含 `PermanentPasswords` 数组
- ✅ 导出密码本时，包含所有已保存的密码
- ✅ 关闭并重新打开程序，密码数据持久化保存
- ✅ 密码使用次数和时间统计正确保存
- ✅ 密码按综合评分排序，常用密码优先测试
