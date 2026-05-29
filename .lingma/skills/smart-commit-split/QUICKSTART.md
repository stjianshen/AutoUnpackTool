# 🚀 快速开始指南

## 5 分钟上手 Smart Commit Split

### 前置条件

- ✅ 已安装 Git
- ✅ 有未提交的变更
- ✅ 在项目根目录

### 第一步：查看当前变更

```bash
git status
```

你应该看到类似输出：
```
Changes not staged for commit:
        modified:   MainWindow.xaml.cs
        modified:   SevenZipExtractor.cs
```

### 第二步：触发技能

在对话中告诉 AI：

```
帮我分析当前的变更并智能拆分提交
```

### 第三步：查看分析结果

AI 会自动分析并展示分组方案，例如：

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

### 第四步：确认并提交

回复 `yes`，AI 将自动：

1. 为每个分组执行 `git add` 和 `git commit`
2. 生成规范的 commit 消息
3. 推送到远程分支

### 第五步：验证结果

```bash
git log --oneline -2
```

查看刚创建的 commit：
```
abc1234 docs: 添加文件占用问题的修复文档
def5678 fix(core): 修复文件占用导致的卡死问题
```

## 常用命令速查

### 只分析不提交

```
帮我分析一下当前的变更，但不要提交
```

### 自定义要求

```
# 使用英文消息
使用英文 commit 消息

# 合并某些改动
将文档更新合并到第一个 commit 中

# 调整分组
请将 UI 相关的改动放在一个 commit
```

### 撤销操作

如果提交后发现问题：

```bash
# 撤销最后一次 commit（保留变更）
git reset --soft HEAD~1

# 修改最后一次 commit 消息
git commit --amend

# 完全撤销（⚠️ 丢弃变更）
git reset --hard HEAD~1
```

## 实际案例

### 案例 1：混合了 Bug 修复和新功能

**场景：**
- 修复了文件删除卡死问题（fix）
- 添加了密码测试日志（feat）

**操作：**
```
用户：帮我分析当前的变更并智能拆分提交

AI：检测到 2 个独立功能...
     【分组 1】fix(core): ...
     【分组 2】feat(password): ...
     是否确认？(yes/no/edit)

用户：yes

结果：创建 2 个独立的 commit
```

### 案例 2：大规模重构

**场景：**
- 重构了路径处理逻辑（3 个文件）
- 更新了相关文档（2 个文件）
- 修复了小 bug（1 个文件）

**操作：**
```
用户：帮我拆分提交

AI：建议拆分为 3 个 commit：
     1. refactor(config): 重构路径处理
     2. docs: 更新文档
     3. fix(ui): 修复小 bug
     
用户：yes

结果：3 个清晰的 commit
```

### 案例 3：只想看看建议

**场景：**
不确定如何拆分，想先看看 AI 的建议

**操作：**
```
用户：帮我分析一下当前的变更，但不要提交

AI：[展示分析结果]
     检测到以下分组...
     （等待进一步指令）

用户：好的，按这个方案提交

AI：[执行提交]
```

## 技巧提示

### 💡 提示 1：预览模式

先让 AI 分析，确认满意后再提交：

```
先分析一下，我看看怎么分组合适
```

### 💡 提示 2：批量合并

如果有多个小改动，可以要求合并：

```
将所有的文档更新合并到一个 commit
```

### 💡 提示 3：精细控制

使用 `edit` 选项手动调整：

```
用户：帮我拆分提交

AI：[展示方案]

用户：edit

用户：请将 MainWindow.xaml.cs 的 UI 改动和核心改动分开
```

### 💡 提示 4：自定义格式

指定 commit 消息风格：

```
使用 Angular 风格的 commit 消息
```

```
commit 消息用英文，body 用中文
```

## 常见问题

### Q1: 如果分组太多怎么办？

**A:** 要求 AI 合并相关的微小改动：
```
请将相关的文档更新合并到一个 commit
```

### Q2: 可以在提交前修改消息吗？

**A:** 可以！在确认前告诉 AI：
```
第一个 commit 的消息改为："fix: resolve file deadlock issue"
```

### Q3: 技能会丢失我的代码吗？

**A:** 不会。Git 是安全的，即使提交错了也可以：
- `git reset --soft` 撤销 commit
- `git reflog` 找回任何操作

### Q4: 如何处理冲突？

**A:** push 前 AI 会提醒你先 pull：
```bash
git pull --rebase origin main
# 解决冲突后
git push origin main
```

### Q5: 可以在其他项目使用吗？

**A:** 这是项目级别的技能，只在当前项目可用。
如需全局使用，复制到 `~/.lingma/skills/`

## 最佳实践

### ✅ 推荐做法

1. **小而频繁**：完成一个功能点就提交
2. **清晰消息**：说明"为什么"而不只是"做了什么"
3. **及时同步**：push 前先 pull 最新代码
4. **审查历史**：定期查看 `git log` 保持清晰

### ❌ 避免做法

1. **大杂烩 commit**：不要把所有改动放一个 commit
2. **模糊消息**：避免 "fix bugs", "update code"
3. **跳过确认**：始终审查 AI 的分组方案
4. **忽略编译**：提交前确保代码可以编译

## 下一步

- 📖 阅读 [examples.md](examples.md) 查看更多场景
- 📋 参考 [reference.md](reference.md) 了解 commit 规范
- 🎬 查看 [DEMO.md](DEMO.md) 看完整演示
- 🔧 运行 `python scripts/analyze_changes.py` 手动分析

## 需要帮助？

如果遇到问题：

1. 查看 [SKILL.md](SKILL.md) 了解详细工作流程
2. 阅读 [README.md](README.md) 获取使用说明
3. 检查 [SKILL_SUMMARY.md](SKILL_SUMMARY.md) 了解技能设计

---

**祝使用愉快！** 🎉

现在试试对你的当前变更使用这个技能吧：

```
帮我分析当前的变更并智能拆分提交
```
