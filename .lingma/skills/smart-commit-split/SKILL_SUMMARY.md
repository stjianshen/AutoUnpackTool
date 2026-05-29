# Smart Commit Split 技能创建完成 ✅

## 技能概述

**名称**: smart-commit-split  
**位置**: `.lingma/skills/smart-commit-split/` (项目级别)  
**描述**: 智能分析 Git 变更并按功能语义拆分成多个 commit 提交

## 核心功能

1. 🔍 **智能分析** - 自动检测和分析所有未提交的变更
2. 🎯 **精准分组** - 按功能语义和变更类型（feat/fix/refactor等）分组
3. 💬 **规范消息** - 生成符合 Conventional Commits 规范的 commit 消息
4. ✅ **交互确认** - 提交前展示方案，等待用户确认后执行
5. 🚀 **自动推送** - 所有 commit 完成后自动推送到远程分支

## 文件结构

```
smart-commit-split/
├── SKILL.md (230 行)           # 核心指令和工作流程
├── README.md (163 行)          # 技能介绍和使用说明
├── examples.md (222 行)        # 实际场景示例
├── reference.md (247 行)       # 快速参考手册
├── DEMO.md (233 行)            # 使用演示
└── scripts/
    └── analyze_changes.py (243 行)  # 变更分析辅助脚本
```

**总计**: 6 个文件，约 1338 行文档和代码

## 设计亮点

### 1. 遵循最佳实践

- ✅ SKILL.md 保持简洁（230 行 < 500 行限制）
- ✅ 使用渐进式披露（详细内容在参考文件中）
- ✅ 描述具体且包含触发词（third-person）
- ✅ 一致的术语和格式

### 2. 智能分组策略

**按变更类型：**
- feat（新功能）
- fix（Bug 修复）
- refactor（重构）
- docs（文档）
- style（格式）
- test（测试）
- chore（杂项）

**按功能模块：**
- ui（界面层）
- core（核心功能）
- password（密码管理）
- config（配置管理）
- queue（队列管理）
- path（路径处理）

### 3. 交互式工作流

```
分析变更 → 生成分组方案 → 用户确认 → 执行提交 → 推送远程
```

用户可以：
- `yes` - 确认方案并执行
- `no` - 取消操作
- `edit` - 手动调整分组

### 4. 辅助工具

提供 Python 脚本 `analyze_changes.py`：
- 自动分析 git diff
- 识别变更类型和模块
- 生成分组建议
- 输出格式化报告

## 使用方法

### 基本用法

```
帮我分析当前的变更并智能拆分提交
```

### 高级用法

```
# 只分析不提交
帮我分析一下当前的变更，但不要提交

# 自定义消息语言
使用英文 commit 消息

# 调整分组策略
将文档更新合并到一个 commit 中
```

## 示例输出

```
检测到以下独立的功能变更：

【分组 1】fix(core): 修复文件占用导致的卡死问题
- 文件：MainWindow.xaml.cs, SevenZipExtractor.cs
- 变更：添加 Windows API 声明，实现静默删除功能
- 影响行数：+719 -175

【分组 2】docs: 添加文件占用问题的修复文档
- 文件：BUGFIX_FILE_OCCUPANCY.md
- 变更：新建 Bug 修复说明文档

是否按以上方案拆分提交？(yes/no/edit)
```

## Commit 规范

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

## 技术实现

### 分析算法

1. **文件状态检测**: `git status --porcelain`
2. **变更统计**: `git diff --numstat`
3. **类型分类**: 基于文件路径 + 关键词匹配
4. **模块识别**: 文件名映射 + 目录分析
5. **消息生成**: 模板 + 上下文信息

### 分组逻辑

```python
# 伪代码
for each changed file:
    change_type = classify_by_keywords(diff_content)
    scope = extract_module(filepath)
    group_key = f"{change_type}|{scope}"
    groups[group_key].add(file)
```

## 适用场景

✅ **适合使用：**
- 同时修改了多个功能模块
- 混合了 Bug 修复和新功能
- 需要清晰的提交历史
- 团队协作的代码审查

❌ **不适合：**
- 单个小的改动
- 紧急 hotfix（直接提交即可）
- WIP 代码（应该用 stash）

## 注意事项

1. ⚠️ 提交前确保代码可以编译
2. ⚠️ 确认当前在正确的分支
3. ⚠️ push 前先 pull 最新代码
4. ⚠️ 不要提交敏感信息
5. ⚠️ 大文件考虑使用 Git LFS

## 扩展性

技能可以轻松扩展：

- 添加新的变更类型识别规则
- 自定义 commit 消息模板
- 集成代码质量检查
- 支持其他版本控制系统
- 添加自动化测试验证

## 相关资源

- [SKILL.md](.lingma/skills/smart-commit-split/SKILL.md) - 完整工作流程
- [examples.md](.lingma/skills/smart-commit-split/examples.md) - 使用示例
- [reference.md](.lingma/skills/smart-commit-split/reference.md) - 快速参考
- [DEMO.md](.lingma/skills/smart-commit-split/DEMO.md) - 实际演示
- [Conventional Commits](https://www.conventionalcommits.org/) - 规范标准

## 下一步

技能已创建完成，可以立即使用！

**测试方法：**

1. 确保有未提交的变更
2. 告诉 AI："帮我分析当前的变更并智能拆分提交"
3. 查看分析结果
4. 确认后执行提交

**改进建议：**

- 根据实际使用反馈调整分组算法
- 添加更多 commit 消息模板
- 优化中文/英文消息生成
- 集成项目特定的规范

---

**创建时间**: 2026-05-29  
**技能版本**: 1.0.0  
**存储位置**: 项目级别 (.lingma/skills/)
