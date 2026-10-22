using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using WindowsMemo.Infrastructure;
using WindowsMemo.Services;

namespace WindowsMemo.Views;

internal sealed class PendingRecoveryWindow : Window
{
    private readonly NoteWorkspace _workspace;
    private readonly ListBox _list = new() { DisplayMemberPath = nameof(PendingNoteFile.Label), MinHeight = 72 };
    private readonly RichTextBox _body = new()
    {
        IsReadOnly = true, IsDocumentEnabled = false, AllowDrop = false,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        Background = NoteTheme.Butter, Foreground = NoteTheme.Ink, FontFamily = NoteTheme.Font, FontSize = NoteTheme.BodySize
    };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 8) };
    private readonly Button _refresh = MakeButton("다시 확인");
    private readonly Button _recover = MakeButton("새 메모로 복구");
    private PendingNotePreview? _preview;
    private int _selectionVersion;
    private bool _closed, _loading, _recovering;
    private static Button MakeButton(string text) => new() { Content = text, MinHeight = NoteTheme.ToolSize, Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(0, 0, 8, 0) };

    internal PendingRecoveryWindow(NoteWorkspace workspace, Action showList)
    {
        _workspace = workspace;
        Title = "Windows 메모 · 미완료 메모 복구";
        Width = 620; Height = 620; MinWidth = 380; MinHeight = 420;
        FontFamily = NoteTheme.Font; Foreground = NoteTheme.Ink;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        var layout = new Grid { Margin = new Thickness(22) };
        foreach (var height in new[] { GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto, new GridLength(2, GridUnitType.Star), GridLength.Auto })
            layout.RowDefinitions.Add(new RowDefinition { Height = height });
        var help = new TextBlock
        {
            Text = "비정상 종료 뒤 남은 내용일 수 있습니다. 선택하여 내용을 확인한 뒤 복구하세요. 기존 메모와 임시 원본은 그대로 두고, 메모 목록에 새 사본을 저장합니다.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12)
        };
        layout.Children.Add(help);
        Grid.SetRow(_list, 1); layout.Children.Add(_list);
        Grid.SetRow(_status, 2); layout.Children.Add(_status);
        Grid.SetRow(_body, 3); layout.Children.Add(_body);
        var actions = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
        actions.Children.Add(_refresh); actions.Children.Add(_recover);
        var list = MakeButton("메모 목록 열기"); list.Click += (_, _) => showList(); actions.Children.Add(list);
        Grid.SetRow(actions, 4); layout.Children.Add(actions); Content = layout;
        _refresh.Click += async (_, _) => await RefreshAsync();
        _list.SelectionChanged += async (_, _) => await PreviewAsync();
        _recover.Click += async (_, _) => await RecoverAsync();
        Loaded += async (_, _) => await RefreshAsync();
        workspace.Changed += OnWorkspaceChanged;
        Closed += (_, _) =>
        {
            _closed = true; _selectionVersion++; _preview = null;
            _body.Document = new FlowDocument(); _list.ItemsSource = null;
            workspace.Changed -= OnWorkspaceChanged;
        };
        UpdateButtons();
    }
    private void OnWorkspaceChanged(object? sender, EventArgs e) => UpdateButtons();
    private void UpdateButtons()
    {
        bool available = !_closed && !_loading && !_recovering && !_workspace.IsBusy;
        _refresh.IsEnabled = _list.IsEnabled = available;
        _recover.IsEnabled = available && _preview is not null;
    }
    internal async Task RefreshAsync()
    {
        if (_closed || _loading || _recovering || _workspace.IsBusy) return;
        _loading = true; _list.SelectedItem = null; _preview = null;
        _status.Text = "남아 있는 파일을 확인하는 중…"; UpdateButtons();
        try
        {
            var scan = await _workspace.ScanPendingAsync();
            if (_closed) return;
            _list.ItemsSource = scan.Files;
            _status.Text = scan.Files.Count == 0 ? "복구 후보가 없습니다." : "파일을 선택하면 읽기 전용으로 내용을 표시합니다.";
            if (scan.Skipped > 0) _status.Text += " 읽을 수 없는 항목은 그대로 보존했습니다.";
            if (scan.Limited) _status.Text += " 처음 64개 항목까지 확인했습니다. 나머지 파일도 보존됩니다.";
        }
        catch (Exception ex) when (NoteWorkspace.IsNoteError(ex))
        { if (!_closed) _status.Text = "목록을 읽지 못했습니다. 파일은 보존됩니다. 다시 시도하세요."; }
        finally { _loading = false; UpdateButtons(); }
    }
    private async Task PreviewAsync()
    {
        int version = ++_selectionVersion; _preview = null; _body.Document = new FlowDocument(); UpdateButtons();
        if (_closed || _list.SelectedItem is not PendingNoteFile file) return;
        _status.Text = "내용을 읽는 중…";
        try
        {
            var preview = await _workspace.PreviewPendingAsync(file.Token);
            if (_closed || version != _selectionVersion) return;
            var document = RtfDocumentCodec.Load(preview.Record.Rtf);
            document.PagePadding = new Thickness(16);
            _body.Document = document; _body.Background = NoteTheme.Paper(preview.Record.Paper); _preview = preview;
            _status.Text = $"저장 시각 {preview.Record.UpdatedAtUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss} · 복구하면 메모 목록에 새 사본으로 저장됩니다.";
        }
        catch (Exception ex) when (NoteWorkspace.IsNoteError(ex))
        { if (!_closed && version == _selectionVersion) _status.Text = "내용을 읽을 수 없습니다. 사용 중이거나 지원하지 않는 파일일 수 있습니다. 원본은 보존했습니다."; }
        finally { UpdateButtons(); }
    }
    private async Task RecoverAsync()
    {
        if (_closed || _recovering || _workspace.IsBusy || _preview is not { } preview) return;
        _recovering = true; UpdateButtons();
        try
        {
            Guid? id = await _workspace.RecoverPendingAsync(preview);
            if (_closed) return;
            _status.Text = id is not null ? "새 사본을 저장했습니다. ‘메모 목록 열기’에서 확인하세요. 원본은 보존됩니다."
                : "복구하지 못했습니다. 파일이 바뀌었거나 저장할 수 없습니다. 다시 선택하여 확인한 뒤 시도하세요. 원본은 보존됩니다.";
            _list.SelectedItem = null; _preview = null;
        }
        finally { _recovering = false; UpdateButtons(); }
    }
}
