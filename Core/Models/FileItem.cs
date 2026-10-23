using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using AutoUnpackTool.Core.Configuration;

namespace AutoUnpackTool.Core.Models
{
    public class FileItem : System.ComponentModel.INotifyPropertyChanged
    {
        private string _fileName = string.Empty;
        private string _filePath = string.Empty;
        private string _status = string.Empty;
        private long _fileSize = 0;
        private string? _foundPassword = null;

        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

        protected virtual void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(propertyName));
        }

        public string FileName
        {
            get => _fileName;
            set
            {
                _fileName = value;
                OnPropertyChanged(nameof(FileName));
                OnPropertyChanged(nameof(DisplayName));
            }
        }

        public string FilePath
        {
            get => _filePath;
            set
            {
                _filePath = value;
                OnPropertyChanged(nameof(FilePath));
            }
        }

        public string Status
        {
            get => _status;
            set
            {
                _status = value;
                OnPropertyChanged(nameof(Status));
            }
        }

        public long FileSize
        {
            get => _fileSize;
            set
            {
                _fileSize = value;
                OnPropertyChanged(nameof(FileSize));
            }
        }

        public string? FoundPassword
        {
            get => _foundPassword;
            set
            {
                _foundPassword = value;
                OnPropertyChanged(nameof(FoundPassword));
            }
        }

        /// <summary>
        /// 当 FileItem 源于文件夹拖入时，记录源文件夹名称用于 UI 显示
        /// </summary>
        private string? _sourceFolderName = null;
        public string? SourceFolderName
        {
            get => _sourceFolderName;
            set
            {
                _sourceFolderName = value;
                OnPropertyChanged(nameof(SourceFolderName));
                OnPropertyChanged(nameof(DisplayName));
            }
        }

        /// <summary>
        /// 用于 UI 显示的名称：如果是从文件夹拖入的，显示文件夹名；否则显示文件名
        /// </summary>
        public string DisplayName => SourceFolderName ?? FileName;

        public ArchiveVolumeInfo? VolumeInfo { get; set; }

        // 新增：父子关系支持
        public ObservableCollection<FileItem> Children { get; set; } = new ObservableCollection<FileItem>();
        public FileItem? Parent { get; set; }
        public bool IsParent => Children.Count > 0;

        /// <summary>
        /// 所属的任务ID（用于批量处理时识别任务范围）
        /// </summary>
        public int TaskId { get; set; } = -1;

        /// <summary>
        /// 当前解压层数（从拖入的文件开始计算，拖入的文件为第1层）
        /// </summary>
        public int CurrentDepth { get; set; } = 1;

        // 新增：子节点完成状态跟踪（用于反向传播）
        private int _expectedChildrenCount = 0;      // 预期的子节点总数
        private int _completedChildrenCount = 0;     // 已完成的子节点数
        private bool _isMarkedComplete = false;      // 是否已标记为完成（防止重复上报）
        private bool _selfExtractSuccess = false;    // 自身压是否成功

        private string? _finalOutputPath = null;

        /// <summary>
        /// 扁平化处理后的最终路径（用于在日志区域上方显示）
        /// </summary>
        public string? FinalOutputPath
        {
            get => _finalOutputPath;
            set
            {
                if (_finalOutputPath != value)
                {
                    _finalOutputPath = value;
                    OnPropertyChanged(nameof(FinalOutputPath));
                }
            }
        }

        /// <summary>
        /// 设置自身解压结果
        /// </summary>
        public void SetSelfExtractResult(bool success)
        {
            _selfExtractSuccess = success;
        }

        /// <summary>
        /// 获取自身解压结果
        /// </summary>
        public bool GetSelfExtractResult()
        {
            return _selfExtractSuccess;
        }

        /// <summary>
        /// 设置预期的子节点数量（在扫描完解压目录后调用）
        /// </summary>
        public void SetExpectedChildrenCount(int count)
        {
            _expectedChildrenCount = count;
            _completedChildrenCount = 0;
            _isMarkedComplete = false;
        }

        /// <summary>
        /// 标记一个子节点完成，并返回是否所有子节点都已完成
        /// </summary>
        public bool MarkChildComplete()
        {
            lock (_lockObject)
            {
                if (_isMarkedComplete)
                    return false;  // 已经标记完成，不再处理

                _completedChildrenCount++;

                // 检查是否所有子节点都已完成
                bool allCompleted = _completedChildrenCount >= _expectedChildrenCount && _expectedChildrenCount > 0;

                if (allCompleted)
                {
                    _isMarkedComplete = true;
                }

                return allCompleted;
            }
        }

        /// <summary>
        /// 获取完成进度信息
        /// </summary>
        public string GetProgressInfo()
        {
            return $"{_completedChildrenCount}/{_expectedChildrenCount}";
        }

        // 用于线程安全的状态标记
        private readonly object _lockObject = new object();
        private bool _isBeingProcessed = false;

        /// <summary>
        /// 尝试获取处理锁，防止多线程竞争
        /// </summary>
        public bool TryLock()
        {
            lock (_lockObject)
            {
                if (_isBeingProcessed)
                    return false;
                _isBeingProcessed = true;
                return true;
            }
        }

        /// <summary>
        /// 释放处理锁
        /// </summary>
        public void ReleaseLock()
        {
            lock (_lockObject)
            {
                _isBeingProcessed = false;
            }
        }

        // 日志存储
        private List<LogEntry> _logs = new List<LogEntry>();
        private readonly object _logLock = new object();

        /// <summary>
        /// 节点关联的日志列表
        /// </summary>
        public List<LogEntry> Logs
        {
            get { lock (_logLock) return new List<LogEntry>(_logs); }
        }

        /// <summary>
        /// 添加日志到当前节点
        /// </summary>
        public void AddLog(string message, ConsoleColor color)
        {
            lock (_logLock)
            {
                _logs.Add(new LogEntry
                {
                    Timestamp = DateTime.Now,
                    Message = message,
                    Color = color
                });
            }
        }

        /// <summary>
        /// 递归收集当前节点及所有子节点的日志
        /// </summary>
        public List<LogEntry> CollectAllLogs()
        {
            var allLogs = new List<LogEntry>();

            lock (_logLock)
            {
                allLogs.AddRange(_logs);
            }

            // 递归收集子节点日志
            foreach (var child in Children)
            {
                allLogs.AddRange(child.CollectAllLogs());
            }

            // 按时间排序
            allLogs.Sort((a, b) => a.Timestamp.CompareTo(b.Timestamp));
            return allLogs;
        }
    }
}
