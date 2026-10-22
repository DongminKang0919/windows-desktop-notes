using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using WindowsMemo.Services;

namespace WindowsMemo.Views;

internal sealed class NoteListWindow : Window
{
    private readonly NoteWorkspace _workspace;
    private readonly bool _trash;
    private readonly bool _archive;
    private readonly ListBox _list = new() { DisplayMemberPath = nameof(NoteListItem.Label) };
    private readonly ObservableCollection<NoteListItem> _items = [];
    private readonly TextBlock _count = new() { Foreground = NoteTheme.MutedInk, FontSize = NoteTheme.MetaSize, Margin = new Thickness(0, 0, 0, 8) };
    private readonly TextBlock _empty = new() { TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Foreground = NoteTheme.MutedInk, Margin = new Thickness(20) };
    private readonly Button _new = Button("새 메모");
    private readonly Button _show;
    private readonly Button _delete;
    private static Button Button(string text) => new() { Content = text, MinHeight = NoteTheme.ToolSize, Padding = NoteTheme.ActionPadding, Margin = new Thickness(0, 8, 8, 0) };
    public NoteListWindow(NoteWorkspace workspace, bool trash = false, bool archive = false)
    {
        _workspace = workspace; _trash = trash; _archive = archive;
        _show = Button(trash || archive ? "선택한 메모 복원" : "선택한 메모 열기");
        _delete = Button(trash ? "영구 삭제" : "휴지통으로 이동");
        Title = trash ? "Windows 메모 · 휴지통" : archive ? "Windows 메모 · 보관함" : "Windows 메모 · 메모 목록";
        Width = 520; Height = 480; MinWidth = 380; MinHeight = 400;
        FontFamily = NoteTheme.Font; FontSize = NoteTheme.BodySize; Foreground = NoteTheme.Ink; Background = NoteTheme.Surface;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        var panel = new Grid { Margin = NoteTheme.WindowPadding };
        foreach (var height in new[] { GridLength.Auto, GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto })
            panel.RowDefinitions.Add(new RowDefinition { Height = height });
        panel.Children.Add(new TextBlock { Text = trash ? "휴지통" : archive ? "보관함" : "메모 목록", FontSize = NoteTheme.TitleSize, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 10) });
        var help = new TextBlock
        {
            Text = trash ? "복원하면 숨김 상태로 메모 목록에 돌아갑니다. 영구 삭제는 되돌릴 수 없습니다. 자동으로 비우지 않습니다."
                : archive ? "보관한 메모입니다. 복원하면 메모 목록으로 돌아갑니다."
                : "숨긴 메모는 다시 열 수 있습니다. 보관한 메모는 트레이의 보관함에 있습니다.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0,0,0,12)
        };
        Grid.SetRow(help, 1); panel.Children.Add(help);
        Grid.SetRow(_count, 2); panel.Children.Add(_count);
        var buttons = new WrapPanel();
        if (!trash && !archive) buttons.Children.Add(_new);
        buttons.Children.Add(_show); buttons.Children.Add(_delete);
        Grid.SetRow(buttons, 4); panel.Children.Add(buttons);
        _list.ItemsSource = _items; _list.DisplayMemberPath = ""; _list.ItemTemplate = ItemTemplate();
        _list.BorderBrush = NoteTheme.Line; _list.Background = SystemColors.WindowBrush;
        _list.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Disabled);
        var style = new Style(typeof(ListBoxItem));
        style.Setters.Add(new Setter(HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(10, 8, 10, 8)));
        style.Setters.Add(new Setter(MinHeightProperty, 52.0));
        style.Setters.Add(new Setter(AutomationProperties.NameProperty, new Binding(nameof(NoteListItem.Label))));
        _list.ItemContainerStyle = style;
        Grid.SetRow(_list, 3); panel.Children.Add(_list);
        _empty.Text = trash ? "휴지통이 비어 있습니다.\n삭제한 메모는 이곳에서 복원할 수 있습니다."
            : archive ? "보관한 메모가 없습니다.\n메모의 더보기에서 보관할 수 있습니다."
            : "메모가 없습니다.\n‘새 메모’로 생각을 적어 보세요.";
        _empty.IsHitTestVisible = false;
        Grid.SetRow(_empty, 3); panel.Children.Add(_empty); Content = panel;
        _new.Click += (_, _) =>
        {
            try { workspace.Create(); }
            catch (Exception ex) when (NoteWorkspace.IsNoteError(ex)) { MessageBox.Show(this, "새 메모를 열지 못했습니다. 기존 내용은 유지됩니다.", "Windows 메모"); }
        };
        _show.Click += async (_, _) => await ShowSelectedAsync();
        _delete.Click += async (_, _) => await DeleteSelectedAsync();
        _list.MouseDoubleClick += async (_, e) =>
        {
            if (e.ChangedButton == MouseButton.Left && e.OriginalSource is DependencyObject source
                && ItemsControl.ContainerFromElement(_list, source) is ListBoxItem) await ShowSelectedAsync();
        };
        _list.KeyDown += async (_, e) =>
        {
            if (e.Key != Key.Enter || Keyboard.Modifiers != ModifierKeys.None) return;
            e.Handled = true; await ShowSelectedAsync();
        };
        _list.SelectionChanged += (_, _) => UpdateButtons();
        workspace.Changed += OnChanged;
        Closed += (_, _) => workspace.Changed -= OnChanged;
        Refresh();
    }
    private static DataTemplate ItemTemplate()
    {
        var row = new FrameworkElementFactory(typeof(StackPanel));
        var title = new FrameworkElementFactory(typeof(TextBlock));
        title.SetBinding(TextBlock.TextProperty, new Binding(nameof(NoteListItem.Title)));
        title.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
        title.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        row.AppendChild(title);
        var status = new FrameworkElementFactory(typeof(TextBlock));
        status.SetBinding(TextBlock.TextProperty, new Binding(nameof(NoteListItem.StatusLabel)));
        status.SetValue(TextBlock.ForegroundProperty, NoteTheme.MutedInk);
        status.SetValue(TextBlock.FontSizeProperty, NoteTheme.MetaSize);
        status.SetValue(MarginProperty, new Thickness(0, 4, 0, 0)); row.AppendChild(status);
        return new DataTemplate(typeof(NoteListItem)) { VisualTree = row };
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
        if (_items.Select(item => item.Id).SequenceEqual(items.Select(item => item.Id)))
        {
            for (int i = 0; i < items.Count; i++) if (_items[i] != items[i]) _items[i] = items[i];
        }
        else { _items.Clear(); foreach (var item in items) _items.Add(item); }
        _list.SelectedItem = _items.FirstOrDefault(item => item.Id == selected);
        _count.Text = $"메모 {items.Count}개 · Enter로 열기 / 복원";
        _count.TextWrapping = TextWrapping.Wrap;
        _empty.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateButtons();
    }
}
