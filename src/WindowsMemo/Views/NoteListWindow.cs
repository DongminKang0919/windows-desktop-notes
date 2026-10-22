using System.Windows;
using System.Windows.Controls;
using WindowsMemo.Services;

namespace WindowsMemo.Views;

internal sealed class NoteListWindow : Window
{
    private readonly NoteWorkspace _workspace;
    private readonly bool _trash;
    private readonly bool _archive;
    private readonly ListBox _list = new() { DisplayMemberPath = nameof(NoteListItem.Label) };
    private readonly Button _new = Button("새 메모");
    private readonly Button _show;
    private readonly Button _delete;
    private static Button Button(string text) => new() { Content = text, Padding = new Thickness(12,6,12,6), Margin = new Thickness(4) };
    public NoteListWindow(NoteWorkspace workspace, bool trash = false, bool archive = false)
    {
        _workspace = workspace; _trash = trash; _archive = archive;
        _show = Button(trash || archive ? "선택한 메모 복원" : "선택한 메모 열기");
        _delete = Button(trash ? "영구 삭제" : "휴지통으로 이동");
        Title = trash ? "Windows 메모 · 휴지통" : archive ? "Windows 메모 · 보관함" : "Windows 메모 · 메모 목록";
        Width = 500; Height = 440; MinWidth = 350; MinHeight = 280;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        var panel = new DockPanel { Margin = new Thickness(16) };
        var help = new TextBlock
        {
            Text = trash ? "복원하면 숨김 상태로 메모 목록에 돌아갑니다. 영구 삭제는 되돌릴 수 없습니다. 자동으로 비우지 않습니다."
                : archive ? "보관한 메모입니다. 복원하면 메모 목록으로 돌아갑니다."
                : "숨긴 메모는 다시 열 수 있습니다. 보관한 메모는 트레이의 보관함에 있습니다.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0,0,0,12)
        };
        DockPanel.SetDock(help, Dock.Top); panel.Children.Add(help);
        var buttons = new WrapPanel();
        if (!trash && !archive) buttons.Children.Add(_new);
        buttons.Children.Add(_show); buttons.Children.Add(_delete);
        DockPanel.SetDock(buttons, Dock.Bottom); panel.Children.Add(buttons);
        panel.Children.Add(_list); Content = panel;
        _new.Click += (_, _) =>
        {
            try { workspace.Create(); }
            catch (Exception ex) when (NoteWorkspace.IsNoteError(ex)) { MessageBox.Show(this, "새 메모를 열지 못했습니다. 기존 내용은 유지됩니다.", "Windows 메모"); }
        };
        _show.Click += async (_, _) => await ShowSelectedAsync();
        _delete.Click += async (_, _) => await DeleteSelectedAsync();
        _list.MouseDoubleClick += async (_, _) => await ShowSelectedAsync();
        _list.SelectionChanged += (_, _) => UpdateButtons();
        workspace.Changed += OnChanged;
        Closed += (_, _) => workspace.Changed -= OnChanged;
        Refresh();
    }
    private async Task ShowSelectedAsync()
    {
        if (_workspace.IsBusy || _list.SelectedItem is not NoteListItem item) return;
        bool success = _trash ? await _workspace.RestoreAsync(item.Id) : _archive ? await _workspace.UnarchiveAsync(item.Id) : await _workspace.ShowAsync(item.Id);
        if (!success) MessageBox.Show("메모를 복원하지 못했습니다. 남아 있는 파일은 보존됩니다.", "메모 복원 실패");
    }
    private async Task DeleteSelectedAsync()
    {
        if (_workspace.IsBusy || _list.SelectedItem is not NoteListItem item) return;
        if (_trash)
        {
            var answer = MessageBox.Show(this, "이 메모의 본문·백업·손상 보존본을 영구 삭제할까요? 이 작업은 되돌릴 수 없습니다.",
                "메모 영구 삭제", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
            if (answer != MessageBoxResult.Yes) return;
        }
        bool success = _trash ? await _workspace.PermanentlyDeleteAsync(item.Id, confirmed: true) : await _workspace.TrashAsync(item.Id);
        if (!success) MessageBox.Show(_trash ? "영구 삭제를 끝내지 못했습니다. 남은 파일은 휴지통에서 다시 삭제할 수 있습니다."
            : "휴지통으로 옮기지 못했습니다. 내용을 유지하고 있습니다.", "메모 처리 실패");
    }
    private void OnChanged(object? sender, EventArgs e) => Refresh();
    private void UpdateButtons()
    {
        _new.IsEnabled = !_workspace.IsBusy;
        _show.IsEnabled = _delete.IsEnabled = !_workspace.IsBusy && _list.SelectedItem is not null;
    }
    private void Refresh()
    {
        Guid? selected = (_list.SelectedItem as NoteListItem)?.Id;
        var items = _trash ? _workspace.TrashItems : _archive ? _workspace.ArchiveItems : _workspace.Items;
        _list.ItemsSource = items;
        _list.SelectedItem = items.FirstOrDefault(item => item.Id == selected);
        UpdateButtons();
    }
}
