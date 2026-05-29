---
name: smart-commit-split
description: Analyze git changes and intelligently split them into multiple commits based on functional semantics and change types (bugfix/feat/refactor). Use when the user wants to commit changes, has multiple unrelated modifications, or needs help organizing commits logically.
---

# Smart Commit Split

智能分析 Git 变更并按功能语义拆分成多个 commit 提交。

## 工作流程

### 1. 分析当前变更

首先获取所有修改的文件和变更内容：

```bash
git status
git diff --stat
git diff HEAD
```

### 2. 识别功能分组

分析变更内容，按以下维度分组：

**按变更类型：**
- `feat`: 新功能、新特性
- `fix`: Bug 修复、问题修正
- `refactor`: 代码重构、结构优化
- `docs`: 文档更新
- `style`: 代码格式调整（不影响逻辑）
- `test`: 测试相关变更
- `chore`: 构建、依赖等杂项

**按功能模块：**
- 根据文件路径判断所属模块（如 `MainWindow.xaml.cs` → UI层，`SevenZipExtractor.cs` → 解压核心）
- 根据代码语义判断功能关联（如密码管理、路径处理、队列管理等）

### 3. 生成分组方案

为每个分组生成：
- **Commit 消息**：遵循 Conventional Commits 规范
- **包含文件**：该分组涉及的文件列表
- **变更摘要**：简要说明改动内容

### 4. 交互式确认

向用户展示拆分方案：

```
检测到以下独立的功能变更：

【分组 1】fix: 修复文件占用导致的卡死问题
- 文件：MainWindow.xaml.cs
- 变更：替换 VB.NET API 为 Windows API，添加 SilentDeleteToRecycleBinAsync 方法
- 影响行数：+50 -20

【分组 2】feat: 增强密码测试日志功能
- 文件：MainWindow.xaml.cs, PasswordManagerDialog.xaml.cs
- 变更：添加详细的密码测试日志记录
- 影响行数：+30 -5

是否按以上方案拆分提交？(yes/no/edit)
```

### 5. 执行提交

用户确认后，依次执行：

```bash
# 对每个分组
git add <file1> <file2> ...
git commit -m "<type>(<scope>): <description>"

# 全部提交完成后
git push origin <current-branch>
```

## 关键规则

### Commit 消息规范

遵循 [Conventional Commits](https://www.conventionalcommits.org/)：

```
<type>(<scope>): <description>

[optional body]

[optional footer(s)]
```

**Type 类型：**
- `feat`: 新功能
- `fix`: Bug 修复
- `refactor`: 重构
- `docs`: 文档
- `style`: 格式
- `test`: 测试
- `chore`: 杂项

**Scope（可选）：** 模块名称，如 `ui`, `core`, `password`, `path`

**Description：** 简短描述（不超过 72 字符）

### 分组原则

1. **原子性**：每个 commit 应该是完整且独立的功能单元
2. **相关性**：相关的变更应该在同一 commit 中
3. **可回滚**：每个 commit 都应该可以单独回滚而不破坏构建
4. **清晰性**：commit 消息应该清楚说明"为什么"而不仅是"做了什么"

### 特殊情况处理

**单个文件包含多个功能：**
- 如果可能，使用 `git add -p` 交互式选择代码块
- 如果无法拆分，优先保证功能完整性

**大量小改动：**
- 合并相关的微小改到一个 commit
- 避免创建过多琐碎的 commit

**跨模块的重构：**
- 如果重构涉及多个模块但属于同一目标，放在一个 commit
- 在 commit body 中详细说明影响范围

## 实用脚本

### 查看变更统计

```bash
# 简洁统计
git diff --stat HEAD

# 详细统计（按文件）
git diff --numstat HEAD

# 按模块分组统计
git diff --stat HEAD | grep -E "\.cs$|\.xaml$"
```

### 检查提交历史

```bash
# 查看最近提交
git log --oneline -10

# 查看当前分支状态
git branch -v
```

### 撤销操作

```bash
# 撤销最后一次 commit（保留变更）
git reset --soft HEAD~1

# 完全撤销（丢弃变更）
git reset --hard HEAD~1

# 从 staging 移除文件
git reset HEAD <file>
```

## 示例场景

### 场景 1：混合了 bugfix 和 feat

**变更前：**
- 修复了文件删除时的权限问题（fix）
- 添加了新的密码管理界面（feat）

**拆分方案：**
```bash
# Commit 1
git add MainWindow.xaml.cs
git commit -m "fix(core): resolve file permission issue during deletion"

# Commit 2
git add PasswordManagerDialog.xaml.cs PasswordManagerDialog.xaml
git commit -m "feat(ui): add password management dialog interface"

git push origin main
```

### 场景 2：重构涉及多个文件

**变更前：**
- 重构了路径处理逻辑，涉及 3 个文件
- 同时修复了一个小的 UI bug

**拆分方案：**
```bash
# Commit 1: 重构（主要变更）
git add AppSettings.cs MainWindow.xaml.cs SevenZipExtractor.cs
git commit -m "refactor(path): centralize path handling logic in AppSettings"

# Commit 2: Bug 修复（独立的小改动）
git add MainWindow.xaml.cs
git commit -m "fix(ui): correct button alignment in settings panel"

git push origin develop
```

## 注意事项

1. **提交前检查**：确保代码可以编译通过
2. **测试覆盖**：重要的功能变更应包含测试
3. **分支策略**：确认当前分支正确，避免误推送到主分支
4. **远程同步**：push 前先 pull，处理可能的冲突
5. **大文件处理**：如果有大文件变更，考虑是否需要 LFS

## 完整执行流程

```
1. 运行 git status 和 git diff 了解变更
2. 分析变更内容，识别功能边界
3. 按类型和模块分组
4. 为每组生成 commit 消息
5. 展示方案并等待用户确认
6. 用户确认后依次执行 git add + commit
7. 所有 commit 完成后执行 git push
8. 验证推送结果
```

## 相关资源

- [Conventional Commits 规范](https://www.conventionalcommits.org/)
- [Git 官方文档 - 原子提交](https://git-scm.com/book/en/v2/Distributed-Git-Contributing-to-a-Project#_commit_guidelines)
- [如何编写好的 commit 消息](https://chris.beams.io/posts/git-commit/)
