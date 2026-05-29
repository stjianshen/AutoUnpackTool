# 使用演示

## 当前状态

查看当前的 Git 变更：

```bash
$ git status
On branch master
Your branch is up to date with 'origin/master'.

Changes not staged for commit:
        modified:   MainWindow.xaml.cs
        modified:   SevenZipExtractor.cs

Untracked files:
        .lingma/
        BUGFIX_FILE_OCCUPANCY.md
```

## 使用技能

### 步骤 1：触发技能

告诉 AI：

```
帮我分析当前的变更并智能拆分提交
```

### 步骤 2：AI 分析

AI 会自动执行以下操作：

1. **读取 SKILL.md**：了解智能拆分的工作流程
2. **运行 git 命令**：
   ```bash
   git status
   git diff --stat HEAD
   git diff HEAD
   ```
3. **分析变更内容**：
   - 识别修改的文件：`MainWindow.xaml.cs`, `SevenZipExtractor.cs`
   - 分析变更类型：fix（Bug 修复）
   - 确定功能模块：core（核心功能）、ui（界面）
4. **生成分组方案**

### 步骤 3：展示方案

AI 会展示类似以下的分析结果：

```
检测到以下独立的功能变更：

【分组 1】fix(core): 修复文件占用导致的卡死问题
- 文件：MainWindow.xaml.cs, SevenZipExtractor.cs
- 主要变更：
  * 添加 Windows API 声明（SHFILEOPSTRUCT）
  * 实现 SilentDeleteToRecycleBinAsync 方法
  * 替换 VB.NET FileIO API 调用
  * 优化 SevenZipExtractor 异常处理
- 影响行数：+719 -175

【分组 2】docs: 添加文件占用问题的修复文档
- 文件：BUGFIX_FILE_OCCUPANCY.md
- 变更：新建 Bug 修复说明文档
- 影响行数：新增文档

是否按以上方案拆分提交？(yes/no/edit)
```

### 步骤 4：用户确认

你回复：`yes`

### 步骤 5：执行提交

AI 依次执行：

```bash
# Commit 1: 核心修复
git add MainWindow.xaml.cs SevenZipExtractor.cs
git commit -m "fix(core): 修复文件占用导致的卡死问题

- 添加 Windows SHFileOperation API 声明
- 实现 SilentDeleteToRecycleBinAsync 方法
- 替换 VB.NET FileIO API 为原生 Windows API
- 优化 SevenZipExtractor 异常处理和资源释放

Fixes: 文件删除时程序无响应的问题"

# Commit 2: 文档
git add BUGFIX_FILE_OCCUPANCY.md
git commit -m "docs: 添加文件占用问题的修复文档

- 详细说明问题原因和解决方案
- 记录技术选型和实现细节"

# 推送
git push origin master
```

### 步骤 6：完成

AI 报告结果：

```
✅ 成功创建 2 个 commit 并推送到远程分支

Commit 历史：
* abc1234 docs: 添加文件占用问题的修复文档
* def5678 fix(core): 修复文件占用导致的卡死问题

远程分支已更新：origin/master
```

## 实际效果

查看最终的 commit 历史：

```bash
$ git log --oneline -2
abc1234 docs: 添加文件占用问题的修复文档
def5678 fix(core): 修复文件占用导致的卡死问题
```

每个 commit 都：
- ✅ 聚焦单一功能
- ✅ 消息清晰规范
- ✅ 可以独立回滚
- ✅ 便于代码审查

## 对比传统方式

### ❌ 传统方式（一次性提交）

```bash
git add .
git commit -m "修复了一些问题"
git push
```

**问题：**
- 不清楚具体修复了什么
- 无法单独回滚某个改动
- 代码审查困难
- 历史记录混乱

### ✅ 使用智能拆分

```bash
# AI 自动分析并拆分
git add MainWindow.xaml.cs SevenZipExtractor.cs
git commit -m "fix(core): 修复文件占用导致的卡死问题..."

git add BUGFIX_FILE_OCCUPANCY.md
git commit -m "docs: 添加文件占用问题的修复文档..."

git push
```

**优势：**
- 清晰的 commit 消息
- 原子性的提交
- 易于追溯和回滚
- 专业的提交历史

## 更多场景

### 场景 A：只分析不提交

```
用户：帮我分析一下当前的变更，但不要提交

AI：[展示分析结果]
检测到以下独立的功能变更：
...
（等待进一步指令）
```

### 场景 B：调整分组

```
用户：请将文档合并到第一个 commit 中

AI：好的，调整为单个 commit：

【分组 1】fix(core): 修复文件占用问题并添加文档
- 文件：MainWindow.xaml.cs, SevenZipExtractor.cs, BUGFIX_FILE_OCCUPANCY.md
- 影响行数：+719 -175, +新文档

是否确认？(yes/no)
```

### 场景 C：自定义消息

```
用户：使用英文 commit 消息

AI：好的，使用英文消息：

【分组 1】fix(core): resolve file occupancy deadlock issue
- Files: MainWindow.xaml.cs, SevenZipExtractor.cs
- Changes: Replace VB.NET API with Windows SHFileOperation

确认？(yes/no)
```

## 技巧提示

1. **预览模式**：先让 AI 分析，确认方案后再提交
2. **批量处理**：多个小改动可以要求 AI 合并
3. **精细控制**：使用 `edit` 选项手动调整分组
4. **撤销操作**：如果不满意，可以 `git reset --soft HEAD~N` 撤销

## 常见问题

**Q: 如果 AI 的分组不合理怎么办？**

A: 回复 `edit` 然后手动指定分组，或者描述你希望的分组方式。

**Q: 可以在提交前修改 commit 消息吗？**

A: 可以！在确认前告诉 AI 你想要的消息格式。

**Q: 如何处理大量的变更文件？**

A: AI 会自动按功能和类型分组。如果分组太多，可以要求合并相关的微小改动。

**Q: 技能会自动 push 吗？**

A: 是的，所有 commit 完成后会自动 push。如果你只想 commit 不 push，可以明确说明。
