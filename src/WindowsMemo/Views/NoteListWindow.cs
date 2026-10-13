using System.Windows;
using System.Windows.Controls;
using WindowsMemo.Services;

namespace WindowsMemo.Views;

internal sealed class NoteListWindow : Window
{
    private readonly NoteWorkspace _workspace;
    private readonly ListBox _list = new() { DisplayMemberPath = nameof(NoteListItem.Label) };
    private readonly Button _new = new() { Content = "새 메모", Padding = new Thickness(12,6,12,6), Margin = new Thickness(4) };
    private readonly Button _show = new() { Content = "선택한 메모 열기", Padding = new Thickness(12,6,12,6), Margin = new Thickness(4) };
    public NoteListWindow(NoteWorkspace workspace)
    {
        _workspace = workspace;
        Title = "Windows 메모 · 메모 목록";
        Width = 440; Height = 440; MinWidth = 350; MinHeight = 280;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        var panel = new DockPanel { Margin = new Thickness(16) };
        var help = new TextBlock { Text = "닫은 메모는 삭제되지 않습니다. 선택해 다시 열 수 있습니다.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0,0,0,12) };
        DockPanel.SetDock(help, Dock.Top); panel.Children.Add(help);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        buttons.Children.Add(_new); buttons.Children.Add(_show);
        DockPanel.SetDock(buttons, Dock.Bottom); panel.Children.Add(buttons);
        panel.Children.Add(_list); Content = panel;
        _new.Click += (_, _) =>
        {
            try { workspace.Create(); }
            catch (Exception ex) when (NoteWorkspace.IsNoteError(ex)) { MessageBox.Show(this, "새 메모를 열지 못했습니다. 기존 내용은 유지됩니다.", "Windows 메모"); }
        };
        _show.Click += async (_, _) => await ShowSelectedAsync();
        _list.MouseDoubleClick += async (_, _) => await ShowSelectedAsync();
        _list.SelectionChanged += (_, _) => _show.IsEnabled = !workspace.IsBusy && _list.SelectedItem is not null;
        workspace.Changed += OnChanged;
        Closed += (_, _) => workspace.Changed -= OnChanged;
        Refresh();
    }
    private async Task ShowSelectedAsync()
    {
        if (_workspace.IsBusy || _list.SelectedItem is not NoteListItem item) return;
        if (!await _workspace.ShowAsync(item.Id)) MessageBox.Show("메모를 복원하지 못했습니다. 원본과 백업은 보존됩니다.", "메모 복원 실패");
    }
    private void OnChanged(object? sender, EventArgs e) => Refresh();
    private void Refresh()
    {
        Guid? selected = (_list.SelectedItem as NoteListItem)?.Id;
        var items = _workspace.Items;
        _list.ItemsSource = items;
        _list.SelectedItem = items.FirstOrDefault(item => item.Id == selected);
        _new.IsEnabled = !_workspace.IsBusy;
        _show.IsEnabled = !_workspace.IsBusy && _list.SelectedItem is not null;
    }
}
