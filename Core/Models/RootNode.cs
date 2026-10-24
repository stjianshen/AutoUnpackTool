using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace AutoUnpackTool.Core.Models
{
    /// <summary>
    /// 根节点类
    /// </summary>
    public class RootNode : System.ComponentModel.INotifyPropertyChanged
    {
        private string _title = string.Empty;
        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

        public string Title
        {
            get => _title;
            set
            {
                _title = value;
                PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(Title)));
            }
        }

        public ObservableCollection<FileItem> Children { get; set; } = new ObservableCollection<FileItem>();

        public int FileCount
        {
            get
            {
                int count = 0;
                foreach (var child in Children)
                {
                    count += CountFilesInTree(child);
                }
                return count;
            }
        }

        private int CountFilesInTree(FileItem item)
        {
            int count = 1;
            foreach (var child in item.Children)
            {
                count += CountFilesInTree(child);
            }
            return count;
        }

        public void UpdateTitle()
        {
            Title = $"待解压任务(共{FileCount}个文件)";
        }

        // 日志收集已下沉 LogHub：根节点视图 = 不过滤的全量投影（含系统日志）。
    }
}
