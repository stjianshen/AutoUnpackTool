using System;
using System.IO;
using AutoUnpackTool;

namespace TestExtensionMatching
{
    class Program
    {
        static void Main(string[] args)
        {
            Console.WriteLine("=== 扩展名匹配功能测试 ===\n");
            
            // 加载配置
            var settings = AppSettings.Load();
            
            Console.WriteLine("当前配置：");
            Console.WriteLine($"ArchiveExtensions: {settings.ArchiveExtensions}");
            Console.WriteLine($"ExcludedExtensions: {settings.ExcludedExtensions}\n");
            
            // 测试解析功能
            Console.WriteLine("解析后的扩展名列表：");
            var archiveExts = settings.GetArchiveExtensions();
            Console.WriteLine($"  包含列表 ({archiveExts.Count} 个): {string.Join(", ", archiveExts)}");
            
            var excludedExts = settings.GetExcludedExtensions();
            Console.WriteLine($"  排除列表 ({excludedExts.Count} 个): {string.Join(", ", excludedExts)}\n");
            
            // 测试优先级
            Console.WriteLine("优先级测试：");
            Console.WriteLine($"  .zip 在包含列表中: {archiveExts.Contains(".zip")}");
            Console.WriteLine($"  .zip 在排除列表中: {excludedExts.Contains(".zip")}");
            Console.WriteLine($"  .exe 在包含列表中: {archiveExts.Contains(".exe")}");
            Console.WriteLine($"  .exe 在排除列表中: {excludedExts.Contains(".exe")}\n");
            
            // 模拟文件检测流程
            Console.WriteLine("模拟文件检测流程：");
            TestFileDetection("test.zip", settings);
            TestFileDetection("document.exe", settings);
            TestFileDetection("archive.rar", settings);
            TestFileDetection("image.jpg", settings);
            TestFileDetection("data.7z", settings);
            
            Console.WriteLine("\n=== 测试完成 ===");
        }
        
        static void TestFileDetection(string fileName, AppSettings settings)
        {
            string extension = Path.GetExtension(fileName).ToLowerInvariant();
            var excludedExts = settings.GetExcludedExtensions();
            var archiveExts = settings.GetArchiveExtensions();
            
            Console.Write($"  {fileName,-20} ");
            
            if (excludedExts.Contains(extension))
            {
                Console.WriteLine("→ [跳过] 在排除列表中");
            }
            else if (archiveExts.Contains(extension))
            {
                Console.WriteLine("→ [检测] 在包含列表中，需魔术数验证");
            }
            else
            {
                Console.WriteLine("→ [检测] 不在列表中，直接魔术数检测");
            }
        }
    }
}
