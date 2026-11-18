using System.Windows;
using System.Windows.Controls;
using WindowsMemo.Services;

namespace WindowsMemo.Views;

internal sealed class NoteTitleWindow : Window
{
    private readonly TextBox _title = new() { MaxLength = NoteTitleSummary.MaxLength, MinHeight = 34, Padding = new Thickness(8) };
    internal string Value => _title.Text.Trim();

    internal NoteTitleWindow(string title)
    {
        Title = "메모 제목 변경";
        Width = 400; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = NoteTheme.Surface; Foreground = NoteTheme.Ink; FontFamily = NoteTheme.Font; FontSize = NoteTheme.BodySize;
        var panel = new StackPanel { Margin = NoteTheme.WindowPadding, Background = NoteTheme.Surface };
        panel.Children.Add(new TextBlock { Text = "이 메모의 제목", FontSize = NoteTheme.TitleSize, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 14) });
        _title.Text = title;
        System.Windows.Automation.AutomationProperties.SetName(_title, "메모 제목");
        panel.Children.Add(_title);
        panel.Children.Add(new TextBlock { Text = "최대 80자. 비워 두면 본문 첫 줄을 제목으로 사용합니다.", TextWrapping = TextWrapping.Wrap, Foreground = NoteTheme.MutedInk, Margin = new Thickness(0, 8, 0, 12) });
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = System.Windows.Media.Brushes.Firebrick };
        panel.Children.Add(error);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "취소", IsCancel = true, Padding = NoteTheme.ActionPadding, Margin = new Thickness(0, 8, 8, 0) };
        var save = new Button { Content = "적용", IsDefault = true, Padding = NoteTheme.ActionPadding, Margin = new Thickness(0, 8, 0, 0) };
        save.Click += (_, _) =>
        {
            if (!NoteTitleSummary.IsValidCustomTitle(Value)) { error.Text = "줄바꿈 없이 80자 이내로 입력하세요."; return; }
            DialogResult = true;
        };
        buttons.Children.Add(cancel); buttons.Children.Add(save); panel.Children.Add(buttons);
        Content = panel;
        Loaded += (_, _) => { _title.Focus(); _title.SelectAll(); };
    }
}
