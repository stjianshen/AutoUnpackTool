using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace AutoUnpackTool.Services
{
    /// <summary>
    /// 把 LogEntry 的 ConsoleColor 映射为文本画刷，替代原来 TextBox 纯文本着色。
    /// Green→Success、Cyan→Info、Yellow→Warning、Red→Error、Gray/Blue/Magenta→Normal。
    /// </summary>
    public sealed class ConsoleColorToBrushConverter : IValueConverter
    {
        private static readonly SolidColorBrush NormalBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33)));
        private static readonly SolidColorBrush InfoBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x00, 0x80, 0x80)));
        private static readonly SolidColorBrush SuccessBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x00, 0x80, 0x00)));
        private static readonly SolidColorBrush WarningBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x9A, 0x63, 0x00)));
        private static readonly SolidColorBrush ErrorBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xC0, 0x00, 0x00)));

        private static SolidColorBrush Freeze(SolidColorBrush brush)
        {
            brush.Freeze();
            return brush;
        }

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is ConsoleColor color)
            {
                switch (color)
                {
                    case ConsoleColor.Green: return SuccessBrush;
                    case ConsoleColor.Cyan: return InfoBrush;
                    case ConsoleColor.Yellow: return WarningBrush;
                    case ConsoleColor.Red: return ErrorBrush;
                    default: return NormalBrush;
                }
            }

            return NormalBrush;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotSupportedException();
        }
    }
}
