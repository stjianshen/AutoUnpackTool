# NanaZip权限问题解决方案

## 问题现象

```
解压异常: An error occurred trying to start process 
'C:\Program Files\WindowsApps\40174MouriNaruto.NanaZip_6.0.1650.0_x64__gnj4mf6z9tkrc\NanaZip.Universal.Console.exe' 
with working directory 'E:\learn\AutoUnpackTool'. 拒绝访问。
```

## 根本原因

**NanaZip是UWP应用，安装在WindowsApps目录下，受到Windows沙箱安全限制：**

1. WindowsApps目录有严格的ACL权限控制
2. UWP应用运行在沙箱环境中
3. 外部进程（如AutoUnpackTool）无法直接启动UWP控制台应用
4. 即使以管理员身份运行也可能失败

## 推荐解决方案

### ✅ 方案1：安装标准版7-Zip（最简单可靠）

#### 步骤：

1. **下载7-Zip**
   - 官网：https://www.7-zip.org/
   - 直接下载链接：https://www.7-zip.org/a/7z2409-x64.exe

2. **安装7-Zip**
   - 运行安装程序
   - 默认安装到：`C:\Program Files\7-Zip\`

3. **配置AutoUnpackTool**
   - 打开AutoUnpackTool
   - 点击"软件设置"
   - 修改7z.exe路径为：`C:\Program Files\7-Zip\7z.exe`
   - 点击"保存"

4. **验证**
   - 重新拖拽压缩包测试
   - 应该可以正常解压

#### 优点：
- ✅ 完全兼容，无权限问题
- ✅ 性能更好
- ✅ 支持所有7z功能
- ✅ 免费开源

---

### ⚠️ 方案2：以管理员身份运行（可能有效）

#### 步骤：

1. 关闭AutoUnpackTool
2. 右键点击 `AutoUnpackTool.exe`
3. 选择"以管理员身份运行"
4. 测试解压功能

#### 缺点：
- ❌ 每次都要右键选择
- ❌ 可能仍然失败（UWP限制很严格）
- ❌ 不方便设置开机自启

---

### ⚠️ 方案3：使用NanaZip GUI版本（不确定是否有效）

尝试使用NanaZip的GUI版本而非控制台版本：

1. 查找NanaZipG.exe的路径
2. 在设置中配置为该路径
3. 测试是否可行

**注意：** GUI版本可能也有同样的权限问题。

---

### ❌ 不推荐的方案

#### 修改WindowsApps权限（危险！）

理论上可以修改WindowsApps目录的ACL权限，但：
- ❌ 非常危险，可能破坏系统稳定性
- ❌ Windows更新会重置权限
- ❌ 违反UWP安全模型
- ❌ 可能导致其他UWP应用异常

**强烈不建议这样做！**

---

## 为什么密码测试可以但解压不行？

观察您的日志：
```
[密码测试] [1/96] 测试: 扶她奶茶  ← 成功执行
...
[线程 1] 图un.7z.001: 解压失败 - 拒绝访问  ← 失败
```

**可能的原因：**

1. **不同的调用方式**
   - 密码测试使用 `ProcessStartInfo` 的某些参数可能不同
   - 或者测试时恰好有权限（时机问题）

2. **工作目录影响**
   - 解压时设置了工作目录 `E:\learn\AutoUnpackTool`
   - UWP应用对工作目录敏感

3. **参数差异**
   - 密码测试：`l -p"xxx" -sccUTF-8 "file"`
   - 解压：`x "file" -o"dir" -y -sccUTF-8`
   - 不同的命令可能有不同的权限要求

**但无论如何，这都是不可靠的行为，不应该依赖。**

---

## 最佳实践建议

### 立即行动：

1. **下载并安装标准7-Zip**
   ```
   https://www.7-zip.org/
   ```

2. **在AutoUnpackTool中配置路径**
   ```
   C:\Program Files\7-Zip\7z.exe
   ```

3. **卸载NanaZip（可选）**
   - 如果不需要UWP版本，可以卸载
   - 保留标准7-Zip即可

### 长期建议：

- 始终使用标准版7-Zip进行自动化任务
- UWP应用适合手动使用，不适合脚本/自动化调用
- 保持工具链的简洁和可靠性

---

## 验证清单

安装标准7-Zip后，请验证：

- [ ] 7z.exe路径配置正确
- [ ] 拖拽压缩包可以正常检测
- [ ] 密码测试正常工作
- [ ] 解压功能正常（无"拒绝访问"错误）
- [ ] 子压缩包递归解压正常
- [ ] 分卷压缩包解压正常

---

## 技术支持

如果安装标准7-Zip后仍有问题，请检查：

1. 7z.exe是否存在于指定路径
2. 是否有杀毒软件阻止
3. 查看完整的错误日志
4. 尝试手动运行7z命令测试

手动测试命令示例：
```cmd
"C:\Program Files\7-Zip\7z.exe" l "D:\test.zip"
"C:\Program Files\7-Zip\7z.exe" x "D:\test.zip" -o"D:\output" -y
```
