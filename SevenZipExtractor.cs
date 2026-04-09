using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace AutoUnpackTool
{
    public class SevenZipExtractor
    {
        private readonly string _sevenZipPath;

        public SevenZipExtractor(string sevenZipPath)
        {
            if (string.IsNullOrWhiteSpace(sevenZipPath) || !File.Exists(sevenZipPath))
            {
                throw new ArgumentException("7z.exe 路径无效", nameof(sevenZipPath));
            }
            _sevenZipPath = sevenZipPath;
        }

        /// <summary>
        /// 测试密码是否正确
        /// </summary>
        public async Task<bool> TestPasswordAsync(string archivePath, string password, Action<string>? onOutput = null, CancellationToken cancellationToken = default)
        {
            return await Task.Run(() =>
            {
                // 检查是否已取消
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    var startInfo = new ProcessStartInfo
                    {
                        FileName = _sevenZipPath,
                        Arguments = $"t -p\"{password}\" \"{archivePath}\"",
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        StandardOutputEncoding = System.Text.Encoding.UTF8,
                        StandardErrorEncoding = System.Text.Encoding.UTF8
                    };

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

                    process.Start();
                    process.BeginOutputReadLine();
                    
                    // 支持取消的等待
                    bool exited = false;
                    while (!process.HasExited)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        
                        if (process.WaitForExit(100))
                        {
                            exited = true;
                            break;
                        }
                    }

                    if (!exited && !process.HasExited)
                    {
                        process.Kill();
                        cancellationToken.ThrowIfCancellationRequested();
                    }

                    // 7z 返回 0 表示成功
                    return process.ExitCode == 0;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"测试密码失败: {ex.Message}");
                    return false;
                }
            }, cancellationToken);
        }

        /// <summary>
        /// 解压文件
        /// </summary>
        public async Task<(bool Success, string Message)> ExtractAsync(
            string archivePath, 
            string outputDir, 
            string? password = null,
            Action<string>? onProgress = null,
            bool showCliWindow = false,
            CancellationToken cancellationToken = default)
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
                        return (false, $"输出路径已存在同名文件: {outputDir}，请更换输出模式");
                    }
                    
                    if (!Directory.Exists(outputDir))
                    {
                        Directory.CreateDirectory(outputDir);
                    }

                    var arguments = $"x \"{archivePath}\" -o\"{outputDir}\" -y";
                    
                    if (!string.IsNullOrEmpty(password))
                    {
                        arguments += $" -p\"{password}\"";
                    }

                    var startInfo = new ProcessStartInfo
                    {
                        FileName = _sevenZipPath,
                        Arguments = arguments,
                        RedirectStandardOutput = !showCliWindow,
                        RedirectStandardError = !showCliWindow,
                        UseShellExecute = false,
                        CreateNoWindow = !showCliWindow
                    };

                    // 只有在重定向输出时才能设置编码
                    if (!showCliWindow)
                    {
                        startInfo.StandardOutputEncoding = System.Text.Encoding.UTF8;
                        startInfo.StandardErrorEncoding = System.Text.Encoding.UTF8;
                    }

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
                    
                    // 等待完成（大文件可能需要更长时间），支持取消
                    bool exited = false;
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
                    }

                    if (!exited && !process.HasExited)
                    {
                        process.Kill();
                        return (false, "解压被取消或超时");
                    }

                    if (process.ExitCode == 0)
                    {
                        return (true, "解压成功");
                    }
                    else
                    {
                        string errorMsg = string.IsNullOrEmpty(error) ? output : error;
                        return (false, $"解压失败 (退出码: {process.ExitCode})\n{errorMsg}");
                    }
                }
                catch (Exception ex)
                {
                    return (false, $"解压异常: {ex.Message}");
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
