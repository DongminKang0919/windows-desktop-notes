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
    public static CornerRadius CornerRadius => new(5);
    public static FontFamily Font { get; } = new("맑은 고딕");
    public static Brush Ink { get; } = Brush("#34342E");
    public static Brush MutedInk { get; } = Brush("#706D58");
    public static Brush Butter { get; } = Brush("#F7E8A3");
    public static Brush Sage { get; } = Brush("#DDE5CA");
    public static Brush Peach { get; } = Brush("#F1D3C2");
    public static Brush Surface { get; } = Brush("#FFFCF3");
    public static Brush Line { get; } = Brush("#E5DFCA");
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
