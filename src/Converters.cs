using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace TodoClock
{
    public class TextDirectionConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
            TimeHelper.IsRtlText(value as string) ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            Binding.DoNothing;
    }

    /// <summary>Project ColorIndex -> gradient brush for the project avatar.</summary>
    public class ProjectBrushConverter : IValueConverter
    {
        private static readonly (string A, string B)[] Pairs =
        {
            ("#22D3EE", "#A855F7"), ("#F472B6", "#A855F7"), ("#34D399", "#22D3EE"),
            ("#FBBF24", "#F472B6"), ("#60A5FA", "#818CF8"), ("#F97316", "#EF4444"),
        };

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            int i = value is int n ? Math.Abs(n) % Pairs.Length : 0;
            var b = new System.Windows.Media.LinearGradientBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(Pairs[i].A),
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(Pairs[i].B), 45);
            b.Freeze();
            return b;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            Binding.DoNothing;
    }
}
