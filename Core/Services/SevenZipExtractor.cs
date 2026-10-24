using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace AutoUnpackTool.Core.Services
{
    public class SevenZipExtractor
    {
        private readonly SevenZipRunner _runner;

        /// <summary>
        /// 由 <see cref="SevenZipExtractorFactory"/> 创建。sevenZipPath 只用于入口校验，
        /// 实际可执行文件由 runner 从 AppSettings 读取（运行期改路径能立即生效），
        /// 所以不要再假定“这里传的路径就是被调用的那个”。
        /// </summary>
        public SevenZipExtractor(string sevenZipPath, SevenZipRunner runner)
        {
            if (string.IsNullOrWhiteSpace(sevenZipPath) || !File.Exists(sevenZipPath))
            {
                throw new ArgumentException("7z.exe 路径无效", nameof(sevenZipPath));
            }
            _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        }

        /// <summary>
        /// 测试密码是否正确
        /// </summary>
        /// <param name="archivePath">压缩包路径</param>
        /// <param name="password">密码</param>
        /// <param name="onOutput">输出回调</param>
        /// <param name="cancellationToken">取消令牌</param>
        /// <param name="useFastMode">使用快速模式（仅验证文件头，适合大文件），默认false（因为很多压缩包只加密内容不加密表头）</param>
        public async Task<bool> TestPasswordAsync(string archivePath, string password, Action<string>? onOutput = null, CancellationToken cancellationToken = default, bool useFastMode = false)
        {
            return await Task.Run(() =>
            {
                // 检查是否已取消
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    // 使用 'l' (list) 命令代替 't' (test) 命令
                    // 'l' 命令只读取文件头，速度极快（秒级）
                    // 't' 命令会完整解压并验证所有文件，大文件极慢
                    string command = useFastMode ? "l" : "t";
                    string args = $"{command} -p\"{password}\" -sccUTF-8 \"{archivePath}\"";
                    var startInfo = _runner.BuildStartInfo(args);

                    using var process = new Process { StartInfo = startInfo };
                    
                    string output = string.Empty;
                    process.OutputDataReceived += (s, e) =>
                    {
                        if (e.Data != null)
                        {
                            output += e.Data + Environment.NewLine;
                            onOutput?.Invoke(e.Data);
                        }
                    };

                    process.ErrorDataReceived += (s, e) =>
                    {
                        if (e.Data != null)
                        {
                            onOutput?.Invoke($"[错误] {e.Data}");
                        }
                    };

                    process.Start();
                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();
                    
                    // 支持取消的等待
                    // 快速模式下，超时时间可以设置得更短（30秒）
                    // 完整测试模式下，保持较长的超时时间（300秒）
                    int timeoutMs = useFastMode ? 30000 : 300000;
                    bool exited = false;
                    int waited = 0;
                    while (!process.HasExited)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        
                        if (process.WaitForExit(100))
                        {
                            exited = true;
                            break;
                        }
                        
                        waited += 100;
                        if (waited > timeoutMs)
                        {
                            onOutput?.Invoke($"密码测试超时（{timeoutMs / 1000}秒），强制终止");
                            break;
                        }
                    }

                    if (!exited && !process.HasExited)
                    {
                        try
                        {
                            process.Kill();
                        }
                        catch { }
                        cancellationToken.ThrowIfCancellationRequested();
                    }

                    // 重要：等待异步输出读取完成，确保 output 变量有完整数据
                    try
                    {
                        // 等待最多2秒让异步读取器处理完剩余输出
                        if (!process.WaitForExit(2000))
                        {
                            onOutput?.Invoke("[警告] 异步输出读取超时");
                        }
                    }
                    catch { }

                    // 快速模式判断逻辑：
                    // 1. 检查是否有错误输出（如 "Wrong password"）
                    // 2. 检查输出中是否包含文件列表（表示成功读取了文件头）
                    if (useFastMode)
                    {
                        string lowerOutput = output.ToLower();
                        string lowerError = output.ToLower();
                        
                        // 检查是否提示需要输入密码（说明密码错误或未提供）
                        if (lowerOutput.Contains("enter password") ||
                            lowerOutput.Contains("enter the password"))
                        {
                            onOutput?.Invoke("[密码测试] 检测到密码提示，密码错误或未提供");
                            return false;
                        }
                        
                        // 检查常见的密码错误提示
                        if (lowerOutput.Contains("wrong password") || 
                            lowerOutput.Contains("错误密码") ||
                            lowerOutput.Contains("密码错误") ||
                            lowerOutput.Contains("data error") ||
                            lowerOutput.Contains("crc failed"))
                        {
                            return false;
                        }
                        
                        // 成功条件：退出码为0 且 成功列出了文件信息
                        // 必须包含文件列表特征，才能证明密码正确
                        if (process.ExitCode == 0 && 
                            (output.Contains("Path =") || output.Contains("Name =") || 
                             output.Contains("Listing archive:") || output.Contains("Date      Time")))
                        {
                            return true;
                        }
                        
                        // 如果退出码不为0，说明测试失败
                        if (process.ExitCode != 0)
                        {
                            return false;
                        }
                        
                        // 如果退出码为0但没有文件列表，说明密码错误
                        // 例如：压缩包头是明文可以读取，但文件列表加密了
                        onOutput?.Invoke($"[密码测试] 退出码=0 但没有文件列表，密码可能错误");
                        return false;
                    }
                    else
                    {
                        // 完整测试模式（t 命令）：
                        // 1. 检查是否有密码错误提示
                        string lowerOutput = output.ToLower();
                        if (lowerOutput.Contains("wrong password") || 
                            lowerOutput.Contains("错误密码") ||
                            lowerOutput.Contains("密码错误") ||
                            lowerOutput.Contains("enter password") ||
                            lowerOutput.Contains("data error") ||
                            lowerOutput.Contains("crc failed"))
                        {
                            return false;
                        }
                        
                        // 2. 退出码0表示测试通过
                        return process.ExitCode == 0;
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    onOutput?.Invoke($"测试密码失败: {ex.Message}");
                    return false;
                }
            }, cancellationToken);
        }

        /// <summary>
        /// 解压文件
        /// </summary>
        /// <param name="extractionTimeout">解压超时时间(秒)，默认3600秒(1小时)</param>
        /// <param name="onProgress">常规进度回调（文本消息）</param>
        /// <param name="onPercentChanged">百分比进度回调（0-100）</param>
        public async Task<(bool Success, string Message, int ExitCode)> ExtractAsync(
            string archivePath, 
            string outputDir, 
            string? password = null,
            Action<string>? onProgress = null,
            Action<int>? onPercentChanged = null,
            bool showCliWindow = false,
            CancellationToken cancellationToken = default,
            int extractionTimeout = 3600,
            string? stegoArchiveType = null)
        {
            return await Task.Run(() =>
            {
                // 检查是否已取消
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    // 检查输出目录是否与现有文件冲突
                    if (File.Exists(outputDir))
                    {
                        return (false, $"输出路径已存在同名文件: {outputDir}，请更换输出模式", -1);
                    }
                    
                    if (!Directory.Exists(outputDir))
                    {
                        Directory.CreateDirectory(outputDir);
                    }

                    // 注意：隐写模式下使用具体类型（如 -tzip/-t7z）而非 -t#
                    // -t# 是哈希扫描模式，只应用于探测，不能用于提取：
                    // 它会把载体自身的字节和误匹配碎片写成 "1"、"2" 这类按序号命名的文件
                    // 类型无法确认时由调用方改为按 Offset/Size 裁剪真实压缩包，不会传 "#" 进来
                    var arguments = $"x \"{archivePath}\" -o\"{outputDir}\" -y -sccUTF-8";
                    if (!string.IsNullOrEmpty(stegoArchiveType) && stegoArchiveType != "#")
                    {
                        arguments += $" -t{stegoArchiveType}";
                    }
                    
                    if (!string.IsNullOrEmpty(password))
                    {
                        arguments += $" -p\"{password}\"";
                    }

                    // 统一交给 runner：showCliWindow 时不重定向、允许窗口显示
                    var startInfo = _runner.BuildStartInfo(arguments, hidden: !showCliWindow);

                    using var process = new Process { StartInfo = startInfo };
                    
                    string output = string.Empty;
                    string error = string.Empty;
                    
                    if (!showCliWindow)
                    {
                        process.OutputDataReceived += (s, e) =>
                        {
                            if (e.Data != null)
                            {
                                output += e.Data + Environment.NewLine;
                                onProgress?.Invoke(e.Data);
                                
                                // 解析进度百分比（格式：31% 24）
                                if (e.Data.Contains("%"))
                                {
                                    try
                                    {
                                        // 提取百分比数字
                                        int percentIndex = e.Data.IndexOf("%");
                                        if (percentIndex > 0)
                                        {
                                            // 向前查找数字
                                            int start = percentIndex - 1;
                                            while (start >= 0 && char.IsDigit(e.Data[start]))
                                            {
                                                start--;
                                            }
                                            string percentStr = e.Data.Substring(start + 1, percentIndex - start - 1);
                                            if (int.TryParse(percentStr, out int percent) && percent >= 0 && percent <= 100)
                                            {
                                                onPercentChanged?.Invoke(percent);
                                            }
                                        }
                                    }
                                    catch { /* 忽略解析错误 */ }
                                }
                            }
                        };

                        process.ErrorDataReceived += (s, e) =>
                        {
                            if (e.Data != null)
                            {
                                error += e.Data + Environment.NewLine;
                            }
                        };
                    }

                    process.Start();
                    
                    if (!showCliWindow)
                    {
                        process.BeginOutputReadLine();
                        process.BeginErrorReadLine();
                    }
                    
                    // 等待完成（大文件可能需要更长时间），支持取消和超时
                    bool exited = false;
                    int waited = 0;
                    int timeoutMs = extractionTimeout * 1000;
                    int lastProgressCheck = 0;
                    
                    while (!process.HasExited)
                    {
                        // 检查是否已取消
                        cancellationToken.ThrowIfCancellationRequested();
                        
                        // 等待100ms后再次检查
                        if (process.WaitForExit(100))
                        {
                            exited = true;
                            break;
                        }
                        
                        waited += 100;
                        
                        // 每30秒输出一次进度提示（避免用户以为卡死）
                        if (waited - lastProgressCheck >= 30000)
                        {
                            onProgress?.Invoke($"[进度] 解压进行中... 已等待 {waited / 1000} 秒");
                            lastProgressCheck = waited;
                        }
                        
                        if (waited > timeoutMs)
                        {
                            onProgress?.Invoke($"解压超时（{extractionTimeout}秒），强制终止进程");
                            break;
                        }
                    }

                    if (!exited && !process.HasExited)
                    {
                        try
                        {
                            onProgress?.Invoke("正在终止超时的解压进程...");
                            process.Kill();
                            process.WaitForExit(5000); // 等待进程完全退出
                        }
                        catch (Exception killEx)
                        {
                            onProgress?.Invoke($"终止进程失败: {killEx.Message}");
                        }
                        return (false, $"解压超时（{extractionTimeout}秒），已强制终止", -1);
                    }

                    // 重要：等待异步输出读取完成，防止缓冲区阻塞导致问题
                    if (!showCliWindow)
                    {
                        try
                        {
                            // 等待最多2秒让异步读取器处理完剩余输出
                            if (!process.WaitForExit(2000))
                            {
                                onProgress?.Invoke("[警告] 异步输出读取超时");
                            }
                        }
                        catch { }
                    }

                    // 7z 退出码：0=成功，1=警告(非致命，部分文件可能失败)，>=2=真失败
                    if (process.ExitCode == 0)
                    {
                        onProgress?.Invoke("解压成功");
                        return (true, "解压成功", 0);
                    }
                    else if (process.ExitCode == 1)
                    {
                        // 警告级：视为部分成功，产物已落盘，交由上层继续扫描处理
                        onProgress?.Invoke("解压完成（存在警告，部分条目可能失败）");
                        return (true, "解压完成，但存在警告（部分文件可能失败）", 1);
                    }
                    else
                    {
                        string errorMsg = string.IsNullOrEmpty(error) ? output : error;
                        onProgress?.Invoke($"解压失败 (退出码: {process.ExitCode})");
                        return (false, $"解压失败 (退出码: {process.ExitCode})\n{errorMsg}", process.ExitCode);
                    }
                }
                catch (Exception ex)
                {
                    return (false, $"解压异常: {ex.Message}", -1);
                }
            });
        }

        /// <summary>
        /// 自动测试密码并解压
        /// </summary>
        public async Task<(bool Success, string Message, string? FoundPassword)> AutoExtractAsync(
            string archivePath,
            string outputDir,
            List<string> passwords,
            Action<string>? onProgress = null,
            Action<string>? onPasswordTest = null,
            Action<string>? onPasswordFound = null)
        {
            // 先尝试无密码解压
            onProgress?.Invoke("尝试无密码解压...");
            var result = await ExtractAsync(archivePath, outputDir, null, onProgress);
            if (result.Success)
            {
                return (true, result.Message, null);
            }

            // 逐个测试密码
            foreach (var password in passwords)
            {
                onPasswordTest?.Invoke(password);
                onProgress?.Invoke($"测试密码: {password}");

                bool isValid = await TestPasswordAsync(archivePath, password);
                
                if (isValid)
                {
                    onPasswordFound?.Invoke(password);
                    onProgress?.Invoke($"找到正确密码: {password}");
                    var extractResult = await ExtractAsync(archivePath, outputDir, password, onProgress);
                    return (extractResult.Success, extractResult.Message, password);
                }

                await Task.Delay(100); // 避免过于频繁
            }

            return (false, "所有密码测试失败", null);
        }
    }
}
