using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace AutoUnpackTool.Services
{
    /// <summary>
    /// 把 LogEntry 的 ConsoleColor 映射为文本画刷，替代原来 TextBox 纯文本着色。
    /// Green→Success、Cyan→Info、Yellow→Warning、Red→Error、Gray/Blue/Magenta→Normal。
    /// 画刷实例故意不 Freeze：主题切换时只改 Color，所有已建立的绑定会立即刷新。
    /// </summary>
    public sealed class ConsoleColorToBrushConverter : IValueConverter
    {
        private static readonly SolidColorBrush NormalBrush = new(Color.FromRgb(0x33, 0x33, 0x33));
        private static readonly SolidColorBrush InfoBrush = new(Color.FromRgb(0x00, 0x78, 0x78));
        private static readonly SolidColorBrush SuccessBrush = new(Color.FromRgb(0x0F, 0x7B, 0x0F));
        private static readonly SolidColorBrush WarningBrush = new(Color.FromRgb(0x9A, 0x63, 0x00));
        private static readonly SolidColorBrush ErrorBrush = new(Color.FromRgb(0xC8, 0x00, 0x0C));

        // 深色底上原配色（深灰/深绿/深橙）会糊成一片，这里换成 Fluent 深色调色板
        private static readonly Color[] LightPalette =
        {
            Color.FromRgb(0x33, 0x33, 0x33),   // Normal
            Color.FromRgb(0x00, 0x78, 0x78),   // Info
            Color.FromRgb(0x0F, 0x7B, 0x0F),   // Success
            Color.FromRgb(0x9A, 0x63, 0x00),   // Warning
            Color.FromRgb(0xC8, 0x00, 0x0C)    // Error
        };

        private static readonly Color[] DarkPalette =
        {
            Color.FromRgb(0xE0, 0xE0, 0xE0),   // Normal
            Color.FromRgb(0x4C, 0xC2, 0xFF),   // Info
            Color.FromRgb(0x6B, 0xE1, 0x6B),   // Success
            Color.FromRgb(0xFF, 0xD4, 0x70),   // Warning
            Color.FromRgb(0xFF, 0x7A, 0x7A)    // Error
        };

        /// <summary>由 ThemeService 在主题切换时调用，原地重调 5 个语义画刷的颜色。</summary>
        public static void ApplyPalette(bool isDark)
        {
            var palette = isDark ? DarkPalette : LightPalette;
            NormalBrush.Color = palette[0];
            InfoBrush.Color = palette[1];
            SuccessBrush.Color = palette[2];
            WarningBrush.Color = palette[3];
            ErrorBrush.Color = palette[4];
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
