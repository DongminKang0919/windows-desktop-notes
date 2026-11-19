using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using WindowsMemo.Infrastructure;
using WindowsMemo.Models;
using WindowsMemo.Services;

namespace WindowsMemo.Views;

internal sealed class SettingsWindow : Window
{
    private readonly SettingsService _settings;
    private readonly StartupService? _startup;
    private readonly ComboBox _paper = new() { MinHeight = 34 };
    private readonly TextBox _width = new() { MinHeight = 32, MaxLength = 4, Width = 90, Padding = new Thickness(6) };
    private readonly TextBox _height = new() { MinHeight = 32, MaxLength = 4, Width = 90, Padding = new Thickness(6) };
    private readonly CheckBox _showList = new() { Content = "직접 실행할 때 메모 목록 열기", Margin = new Thickness(0, 8, 0, 8) };
    private readonly Button _startupButton = new() { Padding = NoteTheme.ActionPadding, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0) };
    private readonly StackPanel _fields = new();
    private readonly Button _save = new() { Content = "설정 저장", Padding = NoteTheme.ActionPadding, IsDefault = true };
    private bool _saving, _closed, _forceClose;
    internal void CloseForShutdown() { _forceClose = true; Close(); }

    internal SettingsWindow(SettingsService settings, StartupService? startup, NoteWorkspace? workspace = null)
    {
        NoteTheme.Apply(this);
        _settings = settings; _startup = startup;
        Title = "Windows 메모 · 설정"; Width = 480; Height = 610; MinWidth = 380; MinHeight = 460;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = NoteTheme.Surface; Foreground = NoteTheme.Ink; FontFamily = NoteTheme.Font; FontSize = NoteTheme.BodySize;
        var grid = new Grid { Margin = NoteTheme.WindowPadding, Background = NoteTheme.Surface };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition());
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.Children.Add(new TextBlock { Text = "설정", FontSize = 24, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 16) });
        var card = new Border { Background = System.Windows.Media.Brushes.White, BorderBrush = NoteTheme.Line,
            BorderThickness = new Thickness(1), CornerRadius = NoteTheme.CornerRadius, Padding = new Thickness(18), Child = _fields };
        var scroll = new ScrollViewer { Content = card, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Grid.SetRow(scroll, 1); grid.Children.Add(scroll);
        _fields.Children.Add(new TextBlock { Text = "새 메모 기본값", FontWeight = FontWeights.SemiBold });
        _fields.Children.Add(new TextBlock { Text = "색상과 크기는 앞으로 만드는 메모에 적용됩니다.", TextWrapping = TextWrapping.Wrap, Foreground = NoteTheme.MutedInk, Margin = new Thickness(0, 6, 0, 12) });
        _fields.Children.Add(new TextBlock { Text = "종이색", Margin = new Thickness(0, 0, 0, 6) });
        foreach (var (label, value) in new[] { ("버터", "Butter"), ("세이지", "Sage"), ("피치", "Peach") })
            _paper.Items.Add(new ComboBoxItem { Content = label, Tag = value });
        _paper.SelectedItem = _paper.Items.Cast<ComboBoxItem>().Single(i => (string)i.Tag == settings.Current.DefaultPaper);
        _fields.Children.Add(_paper);
        _fields.Children.Add(new TextBlock { Text = "새 메모 크기", Margin = new Thickness(0, 18, 0, 8) });
        var size = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };
        size.Children.Add(new TextBlock { Text = "너비 ", VerticalAlignment = VerticalAlignment.Center }); size.Children.Add(_width);
        size.Children.Add(new TextBlock { Text = "  × 높이 ", VerticalAlignment = VerticalAlignment.Center }); size.Children.Add(_height);
        _width.Text = settings.Current.NoteWidth.ToString("0", CultureInfo.InvariantCulture);
        _height.Text = settings.Current.NoteHeight.ToString("0", CultureInfo.InvariantCulture);
        System.Windows.Automation.AutomationProperties.SetName(_width, "새 메모 너비");
        System.Windows.Automation.AutomationProperties.SetName(_height, "새 메모 높이");
        System.Windows.Automation.AutomationProperties.SetName(_paper, "새 메모 종이색");
        _fields.Children.Add(size);
        _fields.Children.Add(new TextBlock { Text = "너비 260–1200 · 높이 200–1200", FontSize = NoteTheme.MetaSize, Foreground = NoteTheme.MutedInk, Margin = new Thickness(0, 4, 0, 14) });
        _showList.IsChecked = settings.Current.ShowListOnLaunch;
        _fields.Children.Add(_showList);
        _fields.Children.Add(new Separator { Margin = new Thickness(0, 10, 0, 12) });
        _fields.Children.Add(new TextBlock { Text = "Windows 시작", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8) });
        _fields.Children.Add(_startupButton);
        _fields.Children.Add(new TextBlock { Text = "자동 실행은 버튼을 누르면 바로 변경됩니다.", TextWrapping = TextWrapping.Wrap, FontSize = NoteTheme.MetaSize, Foreground = NoteTheme.MutedInk, Margin = new Thickness(0, 6, 0, 0) });
        if (workspace is not null)
        {
            _fields.Children.Add(new Separator { Margin = new Thickness(0, 10, 0, 12) });
            _fields.Children.Add(new TextBlock { Text = "메모 백업 및 복원", FontWeight = FontWeights.SemiBold });
            var actions = new WrapPanel();
            foreach (bool restore in new[] { false, true })
            {
                var button = new Button { Content = restore ? "백업에서 복원" : "백업 만들기",
                    Padding = NoteTheme.ActionPadding, Margin = new Thickness(0, 8, 8, 0) };
                button.Click += async (_, _) => await RunBackupAsync(workspace, restore);
                actions.Children.Add(button);
            }
            _fields.Children.Add(actions);
            _fields.Children.Add(new TextBlock { Text = "같은 Windows 계정에서 복원할 수 있습니다. 복원은 기존 메모를 보존하고 숨긴 사본을 추가합니다.",
                Foreground = NoteTheme.MutedInk, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) });
        }
        _fields.Children.Add(_status);
        var footer = new DockPanel { Margin = new Thickness(0, 16, 0, 0) };
        var close = new Button { Content = "닫기", IsCancel = true, Padding = NoteTheme.ActionPadding, Margin = new Thickness(0, 0, 8, 0) };
        close.Click += (_, _) => Close();
        _save.HorizontalAlignment = HorizontalAlignment.Right;
        _save.Style = (Style)FindResource("PrimaryButton");
        _status.Foreground = NoteTheme.MutedInk;
        footer.Children.Add(close); footer.Children.Add(_save);
        Grid.SetRow(footer, 2); grid.Children.Add(footer); Content = grid;
        _save.Click += Save;
        _startupButton.Click += ToggleStartup;
        Activated += (_, _) => RefreshStartup();
        Closed += (_, _) => _closed = true;
        Closing += (_, e) => { if (_saving && !_forceClose) e.Cancel = true; };
        if (settings.LoadFailed) _status.Text = "이전 설정을 읽지 못해 기본값을 표시합니다. 메모는 그대로 유지됩니다. 저장하면 새 설정이 적용됩니다.";
        RefreshStartup();
    }

    private async Task RunBackupAsync(NoteWorkspace workspace, bool restore)
    {
        if (_saving || workspace.IsBusy) return;
        const string filter = "Windows 메모 백업 (*.wmbackup)|*.wmbackup";
        FileDialog dialog = restore
            ? new OpenFileDialog { Filter = filter }
            : new SaveFileDialog { Filter = filter, DefaultExt = ".wmbackup", FileName = $"WindowsMemo-{DateTime.Now:yyyyMMdd-HHmmss}", OverwritePrompt = false };
        if (dialog.ShowDialog(this) != true) return;
        if (restore && MessageBox.Show(this, "백업의 메모를 사본으로 추가할까요? 현재 메모는 유지됩니다.",
                "백업에서 복원", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        _saving = true; _fields.IsEnabled = _save.IsEnabled = false;
        _status.Text = restore ? "백업을 검사하고 있습니다…" : "백업을 만들고 있습니다…";
        try
        {
            if (restore)
            {
                int? count = await workspace.RestoreBackupAsync(dialog.FileName);
                _status.Text = count switch
                {
                    null => "현재 메모를 저장하지 못했습니다. 저장 후 다시 시도하세요.",
                    0 => "새로 복원할 메모가 없습니다.",
                    _ => $"메모 {count}개를 복원했습니다. 메모 목록·보관함·휴지통에서 확인하세요."
                };
            }
            else
            {
                _status.Text = await workspace.ExportBackupAsync(dialog.FileName)
                    ? "메모 백업을 만들었습니다."
                    : "현재 메모를 저장하지 못했습니다. 저장 후 다시 시도하세요.";
            }
        }
        catch (Exception ex) when (NoteWorkspace.IsNoteError(ex))
        {
            _status.Text = restore
                ? "복원을 완료하지 못했습니다. 현재 메모는 보존됩니다. 파일과 Windows 계정을 확인한 뒤 같은 백업으로 다시 시도하세요."
                : "백업을 만들지 못했습니다. 새 파일 이름, 폴더 권한과 메모 상태를 확인하세요.";
        }
        finally { _saving = false; if (!_closed) _fields.IsEnabled = _save.IsEnabled = true; }
    }

    private async void Save(object sender, RoutedEventArgs args)
    {
        if (_saving) return;
        if (!int.TryParse(_width.Text, out int width) || !int.TryParse(_height.Text, out int height)
            || width is < 260 or > 1200 || height is < 200 or > 1200)
        { _status.Text = "너비는 260–1200, 높이는 200–1200 사이의 숫자를 입력하세요."; return; }
        var value = new AppSettings(DefaultPaper: (string)((ComboBoxItem)_paper.SelectedItem).Tag,
            NoteWidth: width, NoteHeight: height, ShowListOnLaunch: _showList.IsChecked == true);
        _saving = true; _fields.IsEnabled = _save.IsEnabled = false;
        try
        {
            await _settings.SaveAsync(value);
            if (!_closed) _status.Text = "설정을 저장했습니다. 새 메모부터 기본 색상과 크기가 적용됩니다.";
        }
        catch (Exception ex) when (FileNoteRepository.IsStorageError(ex))
        { if (!_closed) _status.Text = "설정을 저장하지 못했습니다. 이전 설정을 유지합니다. 폴더 권한과 디스크 공간을 확인하고 다시 시도하세요."; }
        finally { _saving = false; if (!_closed) _fields.IsEnabled = _save.IsEnabled = true; }
    }

    private void RefreshStartup()
    {
        _startupButton.IsEnabled = _startup is not null;
        if (_startup is null) { _startupButton.Content = "진단 모드에서는 자동 실행 변경 불가"; return; }
        try
        {
            _startupButton.Content = _startup.GetState() switch
            { StartupState.Disabled => "로그인 시 자동 실행 켜기", StartupState.Enabled => "로그인 시 자동 실행 끄기", _ => "다른 경로의 자동 실행 등록 해제" };
        }
        catch (Exception ex) when (StartupService.IsStartupError(ex))
        { _startupButton.Content = "자동 실행 상태를 확인하지 못했습니다"; _startupButton.IsEnabled = false; }
    }
    private void ToggleStartup(object sender, RoutedEventArgs args)
    {
        if (_startup is null) return;
        try
        {
            _startup.SetEnabled(_startup.GetState() == StartupState.Disabled);
            _status.Text = "자동 실행 설정을 변경했습니다.";
        }
        catch (Exception ex) when (StartupService.IsStartupError(ex)) { _status.Text = "자동 실행 설정을 변경하지 못했습니다."; }
        RefreshStartup();
    }
}
