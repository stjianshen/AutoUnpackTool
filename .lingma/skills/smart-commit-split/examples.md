# Smart Commit Split 使用示例

## 基本用法

当需要提交代码时，告诉 AI：

```
帮我分析当前的变更并智能拆分提交
```

AI 将自动：
1. 运行 `git status` 和 `git diff` 了解变更
2. 分析变更内容并按功能分组
3. 生成分组方案并展示给你
4. 等待你确认后执行提交
5. 推送到远程分支

## 实际场景示例

### 场景 1：同时修复了 Bug 和添加了新功能

**你的修改：**
- 修复了文件删除时的权限问题（MainWindow.xaml.cs）
- 添加了密码测试日志功能（MainWindow.xaml.cs, PasswordManagerDialog.xaml.cs）

**AI 分析结果：**

```
检测到以下独立的功能变更：

【分组 1】fix(core): 修复文件占用导致的卡死问题
- 文件：MainWindow.xaml.cs (部分变更)
- 变更内容：
  * 添加 SHFILEOPSTRUCT 结构体和 API 声明
  * 实现 SilentDeleteToRecycleBinAsync 方法
  * 替换 VB.NET API 调用
- 影响行数：+50 -20

【分组 2】feat(password): 增强密码测试日志功能
- 文件：MainWindow.xaml.cs, PasswordManagerDialog.xaml.cs
- 变更内容：
  * 添加详细的密码测试日志记录
  * 优化日志输出格式
- 影响行数：+30 -5

是否按以上方案拆分提交？(yes/no/edit)
```

**你回复：** yes

**AI 执行：**

```bash
# 第一个 commit - Bug 修复
git add MainWindow.xaml.cs
git commit -m "fix(core): 修复文件占用导致的卡死问题

- 添加 Windows API 声明替代 VB.NET API
- 实现静默删除到回收站功能
- 解决文件占用时的卡死问题"

# 第二个 commit - 新功能
git add PasswordManagerDialog.xaml.cs
git commit -m "feat(password): 增强密码测试日志功能

- 添加详细的密码测试过程日志
- 优化日志输出格式便于调试"

# 推送
git push origin main
```

### 场景 2：大规模重构

**你的修改：**
- 重构了路径处理逻辑（AppSettings.cs, MainWindow.xaml.cs）
- 更新了相关文档（CHANGELOG_SMART_PATH.md, README.md）
- 修复了一个小的 UI bug（MainWindow.xaml.cs 的另一处）

**AI 分析结果：**

```
检测到以下独立的功能变更：

【分组 1】refactor(config): 重构路径处理逻辑
- 文件：AppSettings.cs, MainWindow.xaml.cs
- 变更：集中化路径管理，提取公共方法
- 影响行数：+80 -60

【分组 2】docs: 更新智能路径相关文档
- 文件：CHANGELOG_SMART_PATH.md, README.md
- 变更：补充路径处理功能的说明
- 影响行数：+40 -10

【分组 3】fix(ui): 修复设置面板按钮对齐问题
- 文件：MainWindow.xaml.cs
- 变更：修正按钮布局参数
- 影响行数：+5 -3

是否按以上方案拆分提交？(yes/no/edit)
```

**你回复：** yes

**AI 执行：**

```bash
# Commit 1: 重构
git add AppSettings.cs MainWindow.xaml.cs
git commit -m "refactor(config): 重构路径处理逻辑

- 将路径处理逻辑集中到 AppSettings
- 提取智能路径解析为独立方法
- 简化 MainWindow 中的路径处理代码"

# Commit 2: 文档
git add CHANGELOG_SMART_PATH.md README.md
git commit -m "docs: 更新智能路径相关文档

- 补充路径处理功能的使用说明
- 添加常见问题解答"

# Commit 3: Bug 修复
git add MainWindow.xaml.cs
git commit -m "fix(ui): 修复设置面板按钮对齐问题"

# 推送
git push origin develop
```

### 场景 3：单个文件包含多个功能

**你的修改：**
在 MainWindow.xaml.cs 中同时做了：
- 修复了队列重复添加的问题
- 添加了新的拖拽文件夹功能

**AI 分析：**

这种情况下，AI 会尝试：

1. **如果可以语义分离**：建议使用 `git add -p` 交互式选择代码块
2. **如果紧密耦合**：建议合并为一个 commit，但在消息中说明包含的内容

```
检测到 MainWindow.xaml.cs 包含多个功能变更：

选项 A：拆分为两个 commit（推荐）
  - 使用 git add -p 交互式选择代码块
  - 更清晰的提交历史
  
选项 B：合并为一个 commit
  - fix(queue): 修复队列重复添加并支持文件夹拖拽
  - 在 body 中详细说明两项改动

请选择处理方式：(A/B)
```

## 高级技巧

### 手动调整分组

如果 AI 的分组方案不完全符合你的预期，可以回复 `edit` 然后手动指定：

```
请将文件按以下方式分组：

分组 1: MainWindow.xaml.cs, SevenZipExtractor.cs
分组 2: AppSettings.cs, SettingsDialog.xaml.cs
```

### 自定义 Commit 消息

你可以要求 AI 使用特定的 commit 消息格式：

```
使用英文 commit 消息，遵循 Angular 规范
```

### 只分析不提交

如果你只想看看建议，不想立即提交：

```
帮我分析一下当前的变更，但不要提交
```

## 注意事项

1. **编译检查**：提交前确保代码可以编译通过
2. **测试覆盖**：重要功能变更应包含或更新测试
3. **分支确认**：确认当前在正确的分支上
4. **远程同步**：push 前建议先 pull 最新代码
5. **大文件**：如果有大二进制文件，考虑使用 Git LFS

## 常见问题

**Q: 如果分组太多怎么办？**

A: 可以要求 AI 合并相关的微小改动：
```
请将相关的文档更新合并到一个 commit 中
```

**Q: 如何撤销已提交的 commit？**

A: 
```bash
# 撤销最后一次 commit（保留变更）
git reset --soft HEAD~1

# 完全撤销（丢弃变更）
git reset --hard HEAD~1
```

**Q: 可以在提交前预览最终的 commit 历史吗？**

A: 可以要求 AI 展示：
```
提交后最终的 log 会是什么样的？
```
