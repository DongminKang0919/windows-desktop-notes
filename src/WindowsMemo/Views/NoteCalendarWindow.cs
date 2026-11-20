using CultureInfo = System.Globalization.CultureInfo;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using WindowsMemo.Services;

namespace WindowsMemo.Views;

internal sealed class NoteCalendarWindow : Window
{
    private sealed class HasNoteConverter(HashSet<DateOnly> dates) : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
            value is DateTime day && dates.Contains(DateOnly.FromDateTime(day));

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            Binding.DoNothing;
    }

    private readonly NoteWorkspace _workspace;
    private readonly Calendar _calendar = new() { SelectedDate = DateTime.Today, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly Button _open = new() { MinHeight = 32, Padding = new(12, 3, 12, 3) };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private HashSet<DateOnly>? _markedDates;
    private bool _opening, _closed;

    internal NoteCalendarWindow(NoteWorkspace workspace)
    {
        _workspace = workspace;
        Title = "메모 달력";
        FontFamily = NoteTheme.Font;
        FontSize = NoteTheme.BodySize;
        SizeToContent = SizeToContent.Height;
        Width = 340;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        var content = new StackPanel { Margin = new(18) };
        content.Children.Add(_calendar);
        content.Children.Add(new TextBlock
        {
            Text = "굵게 표시된 날짜에 메모가 있습니다.",
            Margin = new(0, 8, 0, 10),
            HorizontalAlignment = HorizontalAlignment.Center
        });
        content.Children.Add(_open);
        content.Children.Add(_status);
        Content = content;

        _calendar.SelectedDatesChanged += (_, _) => { _status.Text = ""; Refresh(); };
        _open.Click += OnOpen;
        _workspace.Changed += OnWorkspaceChanged;
        Refresh();
    }

    private void OnWorkspaceChanged(object? sender, EventArgs e) => Refresh();

    private void Refresh()
    {
        if (_closed) return;
        var dates = _workspace.CalendarDates.ToHashSet();
        if (_markedDates is null || !_markedDates.SetEquals(dates))
        {
            _markedDates = dates;
            var marked = new DataTrigger
            {
                Binding = new Binding { Converter = new HasNoteConverter(dates) },
                Value = true
            };
            marked.Setters.Add(new Setter(FontWeightProperty, FontWeights.Bold));
            var style = new Style(typeof(CalendarDayButton));
            style.Triggers.Add(marked);
            _calendar.CalendarDayButtonStyle = style;
        }

        var note = _calendar.SelectedDate is { } day ? _workspace.CalendarNote(DateOnly.FromDateTime(day)) : null;
        _open.Content = note switch
        {
            { Trashed: true } => "휴지통에서 복원 후 열기",
            { Archived: true } => "보관함에서 복원 후 열기",
            null => "체크리스트 메모 만들기",
            _ => "날짜별 메모 열기"
        };
        _open.IsEnabled = _calendar.SelectedDate.HasValue && !_workspace.IsBusy && !_opening;
    }

    private async void OnOpen(object sender, RoutedEventArgs e)
    {
        if (_opening || _workspace.IsBusy || _calendar.SelectedDate is not { } day) return;
        _opening = true;
        _calendar.IsEnabled = false;
        _status.Text = "";
        Refresh();
        try
        {
            bool opened = await _workspace.OpenCalendarNoteAsync(DateOnly.FromDateTime(day));
            if (_closed) return;
            if (opened) Close();
            else _status.Text = "메모를 열지 못했습니다. 잠시 후 다시 시도하세요.";
        }
        catch (Exception ex) when (NoteWorkspace.IsNoteError(ex))
        {
            if (!_closed) _status.Text = "메모를 열지 못했습니다. 기존 메모는 보존됩니다.";
        }
        finally
        {
            _opening = false;
            if (!_closed) { _calendar.IsEnabled = true; Refresh(); }
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _closed = true;
        _workspace.Changed -= OnWorkspaceChanged;
        base.OnClosed(e);
    }
}
