using System.Collections.Generic;

namespace AutoUnpackTool.Core.Models
{
    /// <summary>
    /// 密码映射表（线程安全）
    /// </summary>
    public class PasswordMap
    {
        private readonly Dictionary<string, string?> _map = new Dictionary<string, string?>();
        private readonly object _lock = new object();

        public void Add(string filePath, string? password)
        {
            lock (_lock)
            {
                _map[filePath] = password;
            }
        }

        public bool TryGetValue(string filePath, out string? password)
        {
            lock (_lock)
            {
                return _map.TryGetValue(filePath, out password);
            }
        }

        public bool ContainsKey(string filePath)
        {
            lock (_lock)
            {
                return _map.ContainsKey(filePath);
            }
        }

        public int Count
        {
            get
            {
                lock (_lock)
                {
                    return _map.Count;
                }
            }
        }

        public bool Remove(string filePath)
        {
            lock (_lock)
            {
                return _map.Remove(filePath);
            }
        }
    }
}
