using System.Windows;
using System.Windows.Media;

namespace WindowsMemo.Views;

// All paper, type and spacing tokens live here (WPF sizes are device-independent pixels).
public static class NoteTheme
{
    public const double Width = 320, Height = 300, CollapsedHeight = 52;
    public static double BodySize => 15;
    public static double TitleSize => 17;
    public static double MetaSize => 13;
    public static double ToolSize => 28;
    public static Thickness ContentPadding => new(22, 4, 22, 12);
    public static CornerRadius CornerRadius => new(10);
    public static FontFamily Font { get; } = new("맑은 고딕");
    public static Brush Ink { get; } = Brush("#252D29");
    public static Brush MutedInk { get; } = Brush("#68736C");
    public static Brush Butter { get; } = Brush("#F8EFCB");
    public static Brush Sage { get; } = Brush("#E4EDDF");
    public static Brush Peach { get; } = Brush("#F5E3D9");
    public static Brush Surface { get; } = Brush("#F7F8F5");
    public static Brush Line { get; } = Brush("#DCE2DB");
    public static Brush Accent { get; } = Brush("#315C49");
    public static Brush Selection { get; } = Brush("#EAF1E9");
    public static Brush Danger { get; } = Brush("#A33B36");
    public static Brush DangerSurface { get; } = Brush("#FCF0ED");
    public static void Apply(Window window)
    {
        window.Resources.MergedDictionaries.Add(new ResourceDictionary
            { Source = new Uri("/WindowsMemo;component/Views/NoteStyles.xaml", UriKind.Relative) });
        window.UseLayoutRounding = true;
        window.SnapsToDevicePixels = true;
        window.Background = Surface; window.Foreground = Ink; window.FontFamily = Font; window.FontSize = BodySize;
    }
    public static Thickness WindowPadding => new(22);
    public static Thickness ActionPadding => new(12, 6, 12, 6);
    public static Brush Paper(string paper) => paper switch { "Sage" => Sage, "Peach" => Peach, _ => Butter };
    public static bool IsPaper(string paper) => paper is "Butter" or "Sage" or "Peach";
    private static SolidColorBrush Brush(string value)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(value));
        brush.Freeze(); return brush;
    }
}
