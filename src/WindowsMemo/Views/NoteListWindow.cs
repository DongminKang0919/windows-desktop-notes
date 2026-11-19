using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using WindowsMemo.Services;

namespace WindowsMemo.Views;

internal sealed class NoteListWindow : Window
{
    internal enum Section { Notes, Archive, Trash }
    private readonly NoteWorkspace _workspace;
    private bool _trash;
    private bool _archive;
    private readonly TextBox _search = new() { MaxLength = 80, MinHeight = 36, ToolTip = "제목 검색 (Ctrl+F)" };
    private readonly ComboBox _filter = new() { MinWidth = 94, Margin = new Thickness(8, 0, 0, 0) };
    private readonly Dictionary<Section, Button> _tabs = [];
    private readonly Dictionary<Section, Guid?> _selection = [];
    private readonly Button _undo = Button("삭제 실행 취소");
    private readonly Button _reveal = Button("복원한 메모 열기");
    private Guid? _restoredId;
    private bool _switching;
    internal Section CurrentSection => _trash ? Section.Trash : _archive ? Section.Archive : Section.Notes;
    private readonly ListBox _list = new() { DisplayMemberPath = nameof(NoteListItem.Label) };
    private readonly ObservableCollection<NoteListItem> _items = [];
    private readonly TextBlock _count = new() { Foreground = NoteTheme.MutedInk, FontSize = NoteTheme.MetaSize, Margin = new Thickness(0, 0, 0, 8) };
    private readonly TextBlock _empty = new() { TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Foreground = NoteTheme.MutedInk, Margin = new Thickness(20) };
    private readonly Button _new = Button("새 메모");
    private readonly Button _show;
    private readonly Button _hide = Button("숨기기");
    private readonly Button _archiveNote = Button("보관하기");
    private readonly Button _delete;
    private readonly TextBlock _feedback = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Border _feedbackPanel;
    private bool _closed;
    private static Button Button(string text) => new() { Content = text, MinHeight = NoteTheme.ToolSize, Padding = NoteTheme.ActionPadding, Margin = new Thickness(0, 8, 8, 0) };
    public NoteListWindow(NoteWorkspace workspace, bool trash = false, bool archive = false)
    {
        NoteTheme.Apply(this);
        _workspace = workspace; _trash = trash; _archive = archive;
        _show = Button(trash || archive ? "선택한 메모 복원" : "선택한 메모 열기");
        _delete = Button(trash ? "영구 삭제" : "휴지통으로 이동");
        _new.Style = (Style)FindResource("PrimaryButton");
        _show.Style = (Style)FindResource(trash || archive ? "PrimaryButton" : typeof(Button));
        _delete.Style = (Style)FindResource("DangerButton");
        _feedbackPanel = new Border { Visibility = Visibility.Collapsed,
            Padding = NoteTheme.ActionPadding, Margin = new Thickness(0, 8, 0, 0), CornerRadius = NoteTheme.CornerRadius };
        AutomationProperties.SetAutomationId(_feedback, "NoteListFeedback");
        AutomationProperties.SetLiveSetting(_feedback, AutomationLiveSetting.Polite);
        Title = trash ? "Windows 메모 · 휴지통" : archive ? "Windows 메모 · 보관함" : "Windows 메모 · 메모 목록";
        Width = 600; Height = 640; MinWidth = 380; MinHeight = 520;
        FontFamily = NoteTheme.Font; FontSize = NoteTheme.BodySize; Foreground = NoteTheme.Ink; Background = NoteTheme.Surface;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        var panel = new Grid { Margin = NoteTheme.WindowPadding, Background = NoteTheme.Surface };
        foreach (var height in new[] { GridLength.Auto, GridLength.Auto, GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto, GridLength.Auto })
            panel.RowDefinitions.Add(new RowDefinition { Height = height });
        var header = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 0, 0, 10) };
        var navigation = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        DockPanel.SetDock(navigation, Dock.Right); header.Children.Add(navigation);
        {
            var settings = new Button { Content = "설정", Height = 32, MinWidth = 56, FontSize = NoteTheme.MetaSize,
                Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(6, 0, 0, 0) };
            settings.Click += (_, _) => { if (!_closed) _workspace.RequestSettings(); };
            navigation.Children.Add(settings);
        }
        header.Children.Add(new TextBlock { Text = "내 메모", FontSize = 24,
            FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center });
        panel.Children.Add(header);
        var tabs = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 12) };
        foreach (var (section, label) in new[] { (Section.Notes, "메모 목록"), (Section.Archive, "보관함"), (Section.Trash, "휴지통") })
        {
            var tab = new Button { Content = label, Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(0, 0, 6, 0) };
            tab.Click += (_, _) => Navigate(section);
            _tabs.Add(section, tab); tabs.Children.Add(tab);
        }
        Grid.SetRow(tabs, 1); panel.Children.Add(tabs);
        var searchRow = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };
        AutomationProperties.SetName(_search, "메모 제목 검색");
        AutomationProperties.SetHelpText(_search, "제목의 일부를 입력하세요. 아래쪽 화살표로 검색 결과로 이동하고 Escape로 검색을 지웁니다.");
        AutomationProperties.SetName(_filter, "메모 상태 필터");
        foreach (string label in new[] { "전체 상태", "표시 중", "숨김", "처리 실패" }) _filter.Items.Add(label);
        _filter.SelectedIndex = 0;
        DockPanel.SetDock(_filter, Dock.Right); searchRow.Children.Add(_filter);
        var searchBox = new Grid();
        searchBox.Children.Add(_search);
        var hint = new TextBlock { Text = "제목 검색  ·  Ctrl+F", Foreground = NoteTheme.MutedInk,
            Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
        searchBox.Children.Add(hint);
        var clear = new Button { Content = "×", Width = 28, MinHeight = 28, Padding = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 4, 0), ToolTip = "검색 지우기", Visibility = Visibility.Collapsed };
        AutomationProperties.SetName(clear, "검색 지우기");
        clear.Click += (_, _) => { _search.Clear(); _search.Focus(); };
        _search.Padding = new Thickness(10, 8, 36, 8); searchBox.Children.Add(clear);
        _search.TextChanged += (_, _) =>
        {
            hint.Visibility = _search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            clear.Visibility = _search.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
            if (!_switching) Refresh();
        };
        _filter.SelectionChanged += (_, _) => { if (!_switching) Refresh(); };
        searchRow.Children.Add(searchBox);
        Grid.SetRow(searchRow, 2); panel.Children.Add(searchRow);
        Grid.SetRow(_count, 3); panel.Children.Add(_count);
        var buttons = new WrapPanel();
        buttons.Children.Add(_new);
        buttons.Children.Add(_show); buttons.Children.Add(_hide); buttons.Children.Add(_archiveNote); buttons.Children.Add(_delete);
        _hide.ToolTip = "바탕화면에서 숨깁니다. 메모 목록에서 다시 열 수 있습니다.";
        _archiveNote.ToolTip = "보관함으로 옮깁니다. 보관함에서 복원할 수 있습니다.";
        AutomationProperties.SetName(_list, "메모 목록");
        AutomationProperties.SetHelpText(_list, "화살표로 선택하고 Enter로 엽니다. 보관함과 휴지통에서는 Enter로 복원합니다.");
        var feedbackContent = new StackPanel(); feedbackContent.Children.Add(_feedback);
        var feedbackActions = new WrapPanel(); feedbackActions.Children.Add(_undo); feedbackActions.Children.Add(_reveal);
        feedbackContent.Children.Add(feedbackActions); _feedbackPanel.Child = feedbackContent;
        _undo.Visibility = _reveal.Visibility = Visibility.Collapsed;
        _undo.Click += async (_, _) => await UndoDeleteAsync();
        _reveal.Click += async (_, _) => await OpenRestoredAsync();
        Grid.SetRow(_feedbackPanel, 5); panel.Children.Add(_feedbackPanel);
        Grid.SetRow(buttons, 6); panel.Children.Add(buttons);
        _list.ItemsSource = _items; _list.DisplayMemberPath = ""; _list.ItemTemplate = ItemTemplate();
        _list.BorderBrush = NoteTheme.Line; _list.Background = Brushes.Transparent;
        _list.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Disabled);
        var style = new Style(typeof(ListBoxItem), (Style)FindResource(typeof(ListBoxItem)));
        style.Setters.Add(new Setter(HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(14, 12, 14, 12)));
        style.Setters.Add(new Setter(MinHeightProperty, 52.0));
        style.Setters.Add(new Setter(AutomationProperties.NameProperty, new Binding(nameof(NoteListItem.Label)) { Mode = BindingMode.OneTime }));
        _list.ItemContainerStyle = style;
        Grid.SetRow(_list, 4); panel.Children.Add(_list);
        _empty.Text = trash ? "휴지통이 비어 있습니다.\n삭제한 메모는 이곳에서 복원할 수 있습니다."
            : archive ? "보관한 메모가 없습니다.\n메모의 더보기에서 보관할 수 있습니다."
            : "메모가 없습니다.\n‘새 메모’로 생각을 적어 보세요.";
        _empty.IsHitTestVisible = false;
        Grid.SetRow(_empty, 4); panel.Children.Add(_empty); Content = panel;
        _new.Click += (_, _) =>
        {
            if (!CanPresent || workspace.IsBusy) return;
            try { workspace.Create(); SetFeedback("새 메모를 만들었습니다."); }
            catch (Exception ex) when (NoteWorkspace.IsNoteError(ex)) { SetFeedback("새 메모를 열지 못했습니다. 기존 내용은 유지됩니다. 다시 시도하세요.", failed: true); }
        };
        _show.Click += async (_, _) => await ShowSelectedAsync();
        _hide.Click += async (_, _) => await HideSelectedAsync();
        _archiveNote.Click += async (_, _) => await ArchiveSelectedAsync();
        _delete.Click += async (_, _) => await DeleteSelectedAsync();
        _list.MouseDoubleClick += async (_, e) =>
        {
            if (e.ChangedButton == MouseButton.Left && e.OriginalSource is DependencyObject source
                && ItemsControl.ContainerFromElement(_list, source) is ListBoxItem) await ShowSelectedAsync();
        };
        _list.KeyDown += async (_, e) =>
        {
            if (e.Key != Key.Enter || Keyboard.Modifiers != ModifierKeys.None) return;
            e.Handled = true;
            if (!e.IsRepeat) await ShowSelectedAsync();
        };
        _list.SelectionChanged += (_, _) => UpdateButtons();
        PreviewKeyDown += OnWindowKeyDown;
        workspace.Changed += OnChanged;
        Closed += (_, _) =>
        {
            _closed = true; workspace.Changed -= OnChanged;
            _list.ItemsSource = null; _items.Clear();
            _feedback.Text = ""; AutomationProperties.SetName(_feedback, "");
            _feedbackPanel.Visibility = Visibility.Collapsed;
        };
        Refresh();
    }
    internal void Navigate(Section section)
    {
        if (!CanPresent || _workspace.IsBusy) return;
        _selection[CurrentSection] = (_list.SelectedItem as NoteListItem)?.Id;
        _trash = section == Section.Trash; _archive = section == Section.Archive;
        _restoredId = null; _feedbackPanel.Visibility = Visibility.Collapsed;
        Refresh();
        if (_selection.TryGetValue(section, out var id)) _list.SelectedItem = _items.FirstOrDefault(item => item.Id == id);
    }
    private async void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        var modifiers = Keyboard.Modifiers;
        if (e.Key == Key.F && modifiers == ModifierKeys.Control)
        { _search.Focus(); _search.SelectAll(); e.Handled = true; }
        else if (_search.IsKeyboardFocusWithin && e.Key == Key.Escape && modifiers == ModifierKeys.None)
        { _search.Clear(); e.Handled = true; }
        else if (_search.IsKeyboardFocusWithin && e.Key == Key.Down && modifiers == ModifierKeys.None && _items.Count > 0)
        { _list.SelectedIndex = 0; FocusListSelection(); e.Handled = true; }
        else if (_list.IsKeyboardFocusWithin && modifiers == ModifierKeys.None && e.Key == Key.Delete && !_trash)
        { e.Handled = true; if (!e.IsRepeat) await DeleteSelectedAsync(); }
    }
    private void FocusListSelection()
    {
        if (_list.SelectedItem is not { } selected)
        {
            _search.Focus();
            return;
        }
        _list.ScrollIntoView(selected);
        _list.UpdateLayout();
        if (_list.ItemContainerGenerator.ContainerFromItem(selected) is ListBoxItem row) row.Focus();
        else _list.Focus();
    }
    private static DataTemplate ItemTemplate()
    {
        var row = new FrameworkElementFactory(typeof(StackPanel));
        var title = new FrameworkElementFactory(typeof(TextBlock));
        title.SetBinding(TextBlock.TextProperty, new Binding(nameof(NoteListItem.Title)) { Mode = BindingMode.OneTime });
        title.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
        title.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        row.AppendChild(title);
        var status = new FrameworkElementFactory(typeof(TextBlock));
        status.SetBinding(TextBlock.TextProperty, new Binding(nameof(NoteListItem.StatusLabel)) { Mode = BindingMode.OneTime });
        status.SetValue(TextBlock.ForegroundProperty, NoteTheme.MutedInk);
        status.SetValue(TextBlock.FontSizeProperty, NoteTheme.MetaSize);
        status.SetValue(MarginProperty, new Thickness(0, 4, 0, 0)); row.AppendChild(status);
        return new DataTemplate(typeof(NoteListItem)) { VisualTree = row };
    }
    private async Task ShowSelectedAsync()
    {
        if (!CanPresent || _workspace.IsBusy || _list.SelectedItem is not NoteListItem item) return;
        SetFeedback(_trash || _archive ? "메모를 복원하고 있습니다…" : "메모를 열고 있습니다…");
        bool success = _trash ? await _workspace.RestoreAsync(item.Id) : _archive ? await _workspace.UnarchiveAsync(item.Id) : await _workspace.ShowAsync(item.Id);
        SetFeedback(success ? _trash || _archive ? "복원했습니다. 아래 버튼으로 메모를 열 수 있습니다." : "메모를 열었습니다."
            : "메모를 열거나 복원하지 못했습니다. 남아 있는 파일은 보존됩니다. 다시 시도하세요.", failed: !success);
        if (CanPresent && success && (_trash || _archive)) { _restoredId = item.Id; _reveal.Visibility = Visibility.Visible; UpdateButtons(); }
    }
    private async Task OpenRestoredAsync()
    {
        if (!CanPresent || _workspace.IsBusy || _restoredId is not { } id) return;
        Reveal(Section.Notes, id);
        await ShowSelectedAsync();
    }
    internal void Reveal(Section section, Guid id)
    {
        if (!CanPresent || _workspace.IsBusy) return;
        _switching = true;
        try { _search.Clear(); _filter.SelectedIndex = 0; }
        finally { _switching = false; }
        Navigate(section);
        _list.SelectedItem = _items.FirstOrDefault(item => item.Id == id);
        FocusListSelection();
    }
    private async Task UndoDeleteAsync()
    {
        if (!CanPresent || _workspace.IsBusy || _workspace.LastTrashedId is not { } id) return;
        bool success = await _workspace.UndoTrashAsync();
        if (!CanPresent) return;
        if (success) Reveal(_workspace.ArchiveItems.Any(item => item.Id == id) ? Section.Archive : Section.Notes, id);
        SetFeedback(success ? "삭제를 취소했습니다. 메모를 선택해 두었습니다." : "삭제를 취소하지 못했습니다. 휴지통의 메모는 유지됩니다. 다시 시도하세요.", failed: !success);
    }
    private async Task DeleteSelectedAsync()
    {
        if (!CanPresent || _workspace.IsBusy || _list.SelectedItem is not NoteListItem item) return;
        if (_trash)
        {
            var answer = MessageBox.Show(this, "이 메모의 본문·백업·손상 보존본을 영구 삭제할까요? 이 작업은 되돌릴 수 없습니다.",
                "메모 영구 삭제", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
            if (answer != MessageBoxResult.Yes) return;
        }
        if (!CanPresent || _workspace.IsBusy) return;
        SetFeedback(_trash ? "영구 삭제를 처리하고 있습니다…" : "휴지통으로 옮기고 있습니다…");
        bool success = _trash ? await _workspace.PermanentlyDeleteAsync(item.Id, confirmed: true) : await _workspace.TrashAsync(item.Id);
        SetFeedback(success ? _trash ? "메모를 영구 삭제했습니다." : "휴지통으로 옮겼습니다. 바로 취소할 수 있습니다."
            : _trash ? "영구 삭제를 끝내지 못했습니다. 남은 파일은 휴지통에서 다시 삭제할 수 있습니다."
            : "휴지통으로 옮기지 못했습니다. 내용을 유지하고 있습니다. 다시 시도하세요.", failed: !success);
    }
    private async Task HideSelectedAsync()
    {
        if (!CanPresent || !_hide.IsEnabled || _list.SelectedItem is not NoteListItem item) return;
        SetFeedback("메모를 숨기고 있습니다…");
        bool success = await _workspace.HideAsync(item.Id);
        SetFeedback(success ? "바탕화면에서 숨겼습니다. 메모 목록에서 다시 열 수 있습니다."
            : "메모를 숨기지 못했습니다. 다시 시도하세요.", failed: !success);
    }
    private async Task ArchiveSelectedAsync()
    {
        if (!CanPresent || !_archiveNote.IsEnabled || _list.SelectedItem is not NoteListItem item) return;
        SetFeedback("보관함으로 옮기고 있습니다…");
        bool success = await _workspace.ArchiveAsync(item.Id);
        SetFeedback(success ? "보관함으로 옮겼습니다. 보관함에서 복원할 수 있습니다."
            : "보관함으로 옮기지 못했습니다. 다시 시도하세요.", failed: !success);
    }
    private bool CanPresent => !_closed && _workspace.SessionEndState != SessionEndState.Disposed;
    private void SetFeedback(string text, bool failed = false)
    {
        if (!CanPresent) return;
        _feedback.Text = text;
        AutomationProperties.SetName(_feedback, text);
        _feedbackPanel.Background = failed ? NoteTheme.Butter : NoteTheme.Sage;
        _feedbackPanel.Visibility = Visibility.Visible;
        _restoredId = null; _reveal.Visibility = Visibility.Collapsed;
        _undo.Visibility = _workspace.CanUndoTrash ? Visibility.Visible : Visibility.Collapsed;
    }
    private void OnChanged(object? sender, EventArgs e) => Refresh();
    private void UpdateButtons()
    {
        _new.IsEnabled = !_workspace.IsBusy;
        _show.IsEnabled = _delete.IsEnabled = !_workspace.IsBusy && _list.SelectedItem is not null;
        _archiveNote.IsEnabled = _show.IsEnabled && CurrentSection == Section.Notes;
        _hide.IsEnabled = _archiveNote.IsEnabled && _list.SelectedItem is NoteListItem { Hidden: false };
        _undo.IsEnabled = !_workspace.IsBusy && _workspace.CanUndoTrash;
        _reveal.IsEnabled = !_workspace.IsBusy && _restoredId is not null;
        foreach (var tab in _tabs.Values) tab.IsEnabled = !_workspace.IsBusy;
    }
    private void Refresh()
    {
        if (_closed) return;
        Guid? selected = (_list.SelectedItem as NoteListItem)?.Id;
        int selectedIndex = _list.SelectedIndex;
        bool listHadFocus = _list.IsKeyboardFocusWithin;
        Title = _trash ? "Windows 메모 · 휴지통" : _archive ? "Windows 메모 · 보관함" : "Windows 메모 · 메모 목록";
        _new.Visibility = _trash || _archive ? Visibility.Collapsed : Visibility.Visible;
        _hide.Visibility = _archiveNote.Visibility = _new.Visibility;
        _delete.ToolTip = _trash ? "복원할 수 없도록 영구 삭제합니다." : "휴지통으로 이동 (Delete)";
        _show.Content = _trash || _archive ? "선택한 메모 복원" : "선택한 메모 열기";
        _show.Style = (Style)FindResource(_trash || _archive ? "PrimaryButton" : typeof(Button));
        _delete.Content = _trash ? "영구 삭제" : "휴지통으로 이동";
        _filter.Visibility = _trash || _archive ? Visibility.Collapsed : Visibility.Visible;
        foreach (var (section, tab) in _tabs)
        { tab.Background = section == CurrentSection ? NoteTheme.Selection : Brushes.White; tab.BorderBrush = section == CurrentSection ? NoteTheme.Accent : NoteTheme.Line; }
        var all = _trash ? _workspace.TrashItems : _archive ? _workspace.ArchiveItems : _workspace.Items;
        string query = _search.Text.Trim();
        var items = all.Where(item => item.Title.Contains(query, StringComparison.CurrentCultureIgnoreCase)
            && ((_trash || _archive ? 0 : _filter.SelectedIndex) switch { 1 => !item.Hidden, 2 => item.Hidden, 3 => item.Error is not null, _ => true })).ToArray();
        if (_items.Select(item => item.Id).SequenceEqual(items.Select(item => item.Id)))
        {
            for (int i = 0; i < items.Length; i++) if (_items[i] != items[i]) _items[i] = items[i];
        }
        else { _items.Clear(); foreach (var item in items) _items.Add(item); }
        _list.SelectedItem = _items.FirstOrDefault(item => item.Id == selected);
        if (listHadFocus && selectedIndex >= 0)
        {
            if (_list.SelectedIndex < 0 && _items.Count > 0)
                _list.SelectedIndex = Math.Min(selectedIndex, _items.Count - 1);
            if (!_list.IsKeyboardFocusWithin || _list.SelectedIndex < 0) FocusListSelection();
        }
        _count.Text = items.Length == all.Count ? $"메모 {items.Length}개" : $"검색 결과 {items.Length}개 · 전체 {all.Count}개";
        _count.TextWrapping = TextWrapping.Wrap;
        _empty.Text = all.Count > 0 && items.Length == 0 ? "일치하는 메모가 없습니다.\n검색어나 상태 필터를 바꿔 보세요."
            : _trash ? "휴지통이 비어 있습니다.\n삭제한 메모는 이곳에서 복원할 수 있습니다."
            : _archive ? "보관한 메모가 없습니다.\n메모 목록에서 ‘보관하기’를 선택할 수 있습니다."
            : "메모가 없습니다.\n‘새 메모’로 생각을 적어 보세요.";
        _empty.Visibility = items.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (!_workspace.CanUndoTrash) _undo.Visibility = Visibility.Collapsed;
        UpdateButtons();
    }
}
