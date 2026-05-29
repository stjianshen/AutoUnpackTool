# Smart Commit Split 技能

智能分析 Git 变更并按功能语义拆分成多个 commit 提交。

## 功能特性

- 🔍 **智能分析**：自动检测当前所有未提交的变更
- 🎯 **精准分组**：按功能语义和变更类型（feat/fix/refactor等）智能分组
- 💬 **规范消息**：生成符合 Conventional Commits 规范的 commit 消息
- ✅ **交互确认**：提交前展示方案，等待用户确认
- 🚀 **自动推送**：所有 commit 完成后自动推送到远程分支

## 使用方法

### 基本用法

直接告诉 AI：

```
帮我分析当前的变更并智能拆分提交
```

AI 将：
1. 分析所有修改的文件
2. 按功能和类型分组
3. 生成分组方案和 commit 消息
4. 等待你确认后执行
5. 依次提交并推送

### 只分析不提交

```
帮我分析一下当前的变更，但不要提交
```

### 自定义要求

```
使用英文 commit 消息
```

```
将文档更新合并到一个 commit 中
```

## 文件结构

```
smart-commit-split/
├── SKILL.md              # 主要指令和流程说明
├── examples.md           # 使用示例和场景
├── reference.md          # 快速参考（commit 规范、命令等）
└── scripts/
    └── analyze_changes.py # 变更分析辅助脚本
```

## 分组策略

### 按变更类型

- `feat` - 新功能
- `fix` - Bug 修复
- `refactor` - 代码重构
- `docs` - 文档更新
- `style` - 代码格式
- `test` - 测试相关
- `chore` - 构建/依赖

### 按功能模块

- `ui` - 界面层（MainWindow, Dialogs）
- `core` - 核心功能（解压、文件处理）
- `password` - 密码管理
- `config` - 配置管理
- `queue` - 队列管理
- `path` - 路径处理

## 示例输出

```
检测到以下独立的功能变更：

【分组 1】fix(core): 修复文件占用导致的卡死问题
- 文件：MainWindow.xaml.cs
- 变更：添加 Windows API 声明，实现静默删除功能
- 影响行数：+50 -20

【分组 2】feat(password): 增强密码测试日志功能
- 文件：MainWindow.xaml.cs, PasswordManagerDialog.xaml.cs
- 变更：添加详细的密码测试日志记录
- 影响行数：+30 -5

是否按以上方案拆分提交？(yes/no/edit)
```

## Commit 消息规范

遵循 [Conventional Commits](https://www.conventionalcommits.org/)：

```
<type>(<scope>): <description>

[optional body]

[optional footer(s)]
```

**示例：**
```
fix(core): 修复文件占用导致的卡死问题

- 替换 VB.NET API 为 Windows SHFileOperation API
- 添加异步删除到回收站功能

Fixes #45
```

## 辅助脚本

### analyze_changes.py

自动分析变更并生成分组建议：

```bash
python .lingma/skills/smart-commit-split/scripts/analyze_changes.py
```

输出格式化的分析报告，包括：
- 变更文件列表
- 按类型和模块的分组
- 每个分组的变更统计
- 建议的 commit 消息

## 注意事项

1. **编译检查**：确保代码可以编译通过后再提交
2. **分支确认**：确认当前在正确的分支上
3. **远程同步**：push 前建议先 pull 最新代码
4. **大文件**：二进制大文件考虑使用 Git LFS
5. **敏感信息**：不要提交密码、密钥等敏感信息

## 撤销操作

如果提交后发现问题：

```bash
# 撤销最后一次 commit（保留变更）
git reset --soft HEAD~1

# 完全撤销（丢弃变更）
git reset --hard HEAD~1

# 修改最后一次 commit 消息
git commit --amend
```

## 相关资源

- [SKILL.md](SKILL.md) - 详细的使用说明和工作流程
- [examples.md](examples.md) - 实际场景示例
- [reference.md](reference.md) - 快速参考手册
- [Conventional Commits 规范](https://www.conventionalcommits.org/)
