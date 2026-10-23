namespace AutoUnpackTool.Core.Configuration
{
    /// <summary>
    /// 黑名单规则（支持通配符与正则表达式），一行一条
    /// </summary>
    public class BlacklistRule : System.ComponentModel.INotifyPropertyChanged
    {
        private bool _enabled = true;
        private bool _isRegex;
        private string _pattern = string.Empty;
        private string _note = string.Empty;

        /// <summary>
        /// 是否启用该规则（停用后保留但不参与匹配）
        /// </summary>
        public bool Enabled
        {
            get => _enabled;
            set { if (_enabled != value) { _enabled = value; OnPropertyChanged(nameof(Enabled)); } }
        }

        /// <summary>
        /// 是否按正则表达式匹配；false 表示按通配符匹配（* 任意字符、? 单字符）
        /// 正则默认忽略大小写，支持 Perl/.NET 常用语法（\d、\w、|、[]、(?i)、环视等）
        /// </summary>
        public bool IsRegex
        {
            get => _isRegex;
            set { if (_isRegex != value) { _isRegex = value; OnPropertyChanged(nameof(IsRegex)); } }
        }

        /// <summary>
        /// 匹配模式。模式包含 / 或 \ 时匹配完整路径，否则只匹配文件名
        /// </summary>
        public string Pattern
        {
            get => _pattern;
            set { if (_pattern != value) { _pattern = value ?? string.Empty; OnPropertyChanged(nameof(Pattern)); } }
        }

        /// <summary>
        /// 规则说明（仅用于展示，便于日后维护）
        /// </summary>
        public string Note
        {
            get => _note;
            set { if (_note != value) { _note = value ?? string.Empty; OnPropertyChanged(nameof(Note)); } }
        }

        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(propertyName));
        }
    }
}
