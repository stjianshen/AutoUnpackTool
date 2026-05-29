# Commit 拆分快速参考

## Conventional Commits 规范

### 基本格式

```
<type>(<scope>): <description>

[optional body]

[optional footer(s)]
```

### Type 类型

| Type | 说明 | 示例 |
|------|------|------|
| `feat` | 新功能 | `feat(ui): add dark mode toggle` |
| `fix` | Bug 修复 | `fix(core): resolve null pointer exception` |
| `refactor` | 代码重构 | `refactor(password): simplify encryption logic` |
| `docs` | 文档更新 | `docs: update installation guide` |
| `style` | 代码格式 | `style: fix indentation in MainWindow` |
| `test` | 测试相关 | `test: add unit tests for path parser` |
| `chore` | 构建/依赖 | `chore: update NuGet packages` |

### Scope 范围（本项目常用）

- `ui` - 界面相关（MainWindow, Dialogs）
- `core` - 核心功能（解压、文件处理）
- `password` - 密码管理
- `config` - 配置管理（AppSettings）
- `queue` - 队列管理
- `path` - 路径处理
- `general` - 通用/跨模块

## 分组原则速查

### ✅ 应该拆分的情况

1. **不同类型的变更**
   - Bug 修复 + 新功能 → 拆分为 2 个 commit
   - 重构 + 文档更新 → 拆分为 2 个 commit

2. **不同功能模块**
   - UI 改动 + 核心逻辑改动 → 拆分
   - 密码管理 + 路径处理 → 拆分

3. **可独立回滚的单元**
   - 每个 commit 应该可以单独 revert

### ❌ 不应拆分的情况

1. **紧密耦合的变更**
   - API 定义 + API 实现 → 同一 commit
   - 接口修改 + 所有实现类调整 → 同一 commit

2. **原子性操作**
   - 重命名方法 + 所有调用处更新 → 同一 commit

3. **微小的相关改动**
   - 修复 typo + 更新相关注释 → 同一 commit

## Commit 消息模板

### Bug 修复

```
fix(<scope>): <简短描述问题>

<详细说明>
- 问题原因
- 解决方案
- 影响范围

Closes #123
```

**示例：**
```
fix(core): 修复文件占用导致的卡死问题

- VB.NET API 在文件占用时会导致程序无响应
- 替换为 Windows SHFileOperation API
- 添加异步删除到回收站功能

Fixes #45
```

### 新功能

```
feat(<scope>): <简短描述功能>

<详细说明>
- 功能特性
- 使用方法
- 注意事项
```

**示例：**
```
feat(password): 添加密码强度检测功能

- 实时显示密码强度指示器
- 支持自定义强度规则
- 弱密码时给出改进建议
```

### 重构

```
refactor(<scope>): <简短描述重构内容>

<详细说明>
- 重构原因
- 主要改动
- 性能/可维护性提升
```

**示例：**
```
refactor(config): 集中化配置管理逻辑

- 将分散的配置读取逻辑统一到 AppSettings
- 提取配置验证为独立方法
- 减少 MainWindow 中的配置相关代码约 200 行
```

## Git 命令速查

### 查看变更

```bash
# 查看状态
git status

# 查看变更统计
git diff --stat HEAD

# 查看详细变更
git diff HEAD

# 查看特定文件
git diff HEAD -- filename.cs
```

### 交互式选择

```bash
# 交互式选择代码块
git add -p

# 交互式选择文件
git add -i
```

### 提交操作

```bash
# 添加文件
git add file1.cs file2.cs

# 提交
git commit -m "type(scope): description"

# 修改最后一次 commit 消息
git commit --amend

# 撤销 commit（保留变更）
git reset --soft HEAD~1
```

### 推送操作

```bash
# 推送到当前分支
git push origin $(git branch --show-current)

# 强制推送（谨慎使用）
git push --force-with-lease

# 先拉取再推送
git pull --rebase origin main
git push origin main
```

## 分析脚本使用

```bash
# 运行分析脚本（Python）
python .lingma/skills/smart-commit-split/scripts/analyze_changes.py

# 输出示例：
# 检测到 5 个文件变更
# 建议拆分为 3 个 commit:
# 
# 【分组 1】fix(core): ...
# 【分组 2】feat(ui): ...
# 【分组 3】docs: ...
```

## 决策流程图

```
开始
  ↓
有未提交的变更？
  ↓ 是
变更是否涉及多个功能？
  ↓ 是
能否清晰分离？
  ├─ 是 → 按功能拆分commit
  └─ 否 → 合并为一个commit，详细说明
  ↓
每个commit是否原子性？
  ├─ 是 → 生成commit消息
  └─ 否 → 进一步细分
  ↓
用户确认方案？
  ├─ 是 → 执行提交
  └─ 否 → 调整方案
  ↓
推送到远程
  ↓
结束
```

## 最佳实践

1. **小而频繁**：多个小 commit 优于一个大 commit
2. **清晰消息**：说明"为什么"而不仅是"做了什么"
3. **单一职责**：每个 commit 只做一件事
4. **及时提交**：完成一个功能点就提交
5. **保持一致**：团队使用统一的 commit 规范

## 检查清单

提交前确认：
- [ ] 代码可以编译通过
- [ ] 相关测试已更新
- [ ] Commit 消息清晰准确
- [ ] 没有遗漏相关文件
- [ ] 没有包含临时文件
- [ ] 在正确的分支上
- [ ] 已同步最新代码（pull）
