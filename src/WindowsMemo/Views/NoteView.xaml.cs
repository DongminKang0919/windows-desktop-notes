using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace WindowsMemo.Views;

public partial class NoteView : UserControl, IDisposable
{
    public const int MaxCharacters = 100_000;
    private bool _disposed;
    private bool _rollingBack;
    private int _textLength;
    private bool _saveNeedsAttention;
    private MenuItem? _collapseItem;
    public string Paper { get; private set; } = "Butter";
    public bool IsCollapsed { get; private set; }
    public static readonly DependencyProperty NoteTitleProperty = DependencyProperty.Register(nameof(NoteTitle), typeof(string), typeof(NoteView), new PropertyMetadata("메모"));
    public string NoteTitle { get => (string)GetValue(NoteTitleProperty); private set => SetValue(NoteTitleProperty, value); }
    public event EventHandler? AppearanceChanged;
    public event EventHandler? ArchiveRequested;
    public event EventHandler? ArchiveListRequested;
    public event EventHandler? DocumentChanged;
    public event EventHandler? SaveRequested;
    public event EventHandler? CloseRequested;
    public event EventHandler? NewRequested;
    public event EventHandler? TrashRequested;
    public event EventHandler? ListRequested;
    public event EventHandler? ReconnectRequested;
    public event EventHandler<Vector>? MoveRequested;
    public event EventHandler<Vector>? ResizeRequested;
    public RichTextBox TextEditor => Editor;

    public NoteView()
    {
        InitializeComponent();
        DataObject.AddPastingHandler(Editor, OnPasting);
        BuildMenu();
        PreviewKeyDown += OnViewKeyDown;
        RefreshTitle();
    }

    public void SetSaveStatus(string text, bool canRetry)
    {
        _saveNeedsAttention = canRetry;
        if (_collapseItem is not null)
        {
            _collapseItem.IsEnabled = !canRetry;
            _collapseItem.ToolTip = canRetry ? "저장에 성공한 뒤 메모를 접을 수 있습니다." : null;
        }
        SaveLabel.Text = text;
        SaveLabel.Visibility = canRetry ? Visibility.Visible : Visibility.Collapsed;
        SaveButton.Visibility = canRetry ? Visibility.Visible : Visibility.Collapsed;
        if (canRetry && IsCollapsed) SetCollapsed(false);
    }
    private void Save_OnClick(object sender, RoutedEventArgs e) => SaveRequested?.Invoke(this, EventArgs.Empty);

    public void SetMode(string text) { ModeLabel.Text = text; ModeLabel.Visibility = Visibility.Visible; }
    public void SetRecoveryMode(string text, bool canRetry)
    {
        SetMode(text);
        ModeLabel.Visibility = canRetry ? Visibility.Visible : Visibility.Collapsed;
        if (canRetry && IsCollapsed) SetCollapsed(false);
        ReconnectButton.Visibility = canRetry ? Visibility.Visible : Visibility.Collapsed;
    }
    public void RequestClose() => CloseRequested?.Invoke(this, EventArgs.Empty);

    public bool InsertPlainText(string text)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(NoteView));
        if (!CanInsert(text.Length))
        {
            NoticeLabel.Text = "메모는 최대 100,000자까지 입력할 수 있습니다.";
            return false;
        }
        Editor.Selection.Text = text;
        Editor.CaretPosition = Editor.Selection.End;
        return true;
    }

    private bool CanInsert(int length) => length <= MaxCharacters &&
        _textLength
        - Editor.Selection.Text.Length + length <= MaxCharacters;

    private void OnPasting(object sender, DataObjectPastingEventArgs e)
    {
        e.CancelCommand();
        if (!e.SourceDataObject.GetDataPresent(DataFormats.UnicodeText, false))
        {
            NoticeLabel.Text = "텍스트만 붙여넣을 수 있습니다.";
            return;
        }
        if (e.SourceDataObject.GetData(DataFormats.UnicodeText, false) is string text)
            InsertPlainText(text);
    }

    private void Editor_OnPreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        if (!CanInsert(e.Text.Length))
        {
            e.Handled = true;
            NoticeLabel.Text = "입력 한도를 초과했습니다.";
        }
    }

    private void Editor_OnTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_rollingBack || Editor is null || NoticeLabel is null) return;
        string text = new TextRange(Editor.Document.ContentStart, Editor.Document.ContentEnd).Text;
        if (text.Length > MaxCharacters && Editor.CanUndo)
        {
            _rollingBack = true;
            try { Editor.Undo(); }
            finally { _rollingBack = false; }
            text = new TextRange(Editor.Document.ContentStart, Editor.Document.ContentEnd).Text;
            NoticeLabel.Text = "입력 한도를 초과하여 마지막 변경을 취소했습니다.";
        }
        else NoticeLabel.Text = "";
        RefreshTitle(text);
        DocumentChanged?.Invoke(this, EventArgs.Empty);
    }

    public void ToggleBold() => Execute(EditingCommands.ToggleBold);
    public void ToggleBullets() => Execute(EditingCommands.ToggleBullets);
    public void ToggleNumbering() => Execute(EditingCommands.ToggleNumbering);
    public void ApplyColor(Brush color)
    {
        Editor.Selection.ApplyPropertyValue(TextElement.ForegroundProperty, color);
        Editor.Focus();
    }
    private void Execute(RoutedUICommand command)
    {
        Editor.Focus();
        if (command.CanExecute(null, Editor)) command.Execute(null, Editor);
    }
    private void Bold_OnClick(object sender, RoutedEventArgs e) => ToggleBold();
    private void Black_OnClick(object sender, RoutedEventArgs e) => ApplyColor(Brushes.Black);
    private void Red_OnClick(object sender, RoutedEventArgs e) => ApplyColor(Brushes.Firebrick);
    private void Blue_OnClick(object sender, RoutedEventArgs e) => ApplyColor(Brushes.RoyalBlue);
    private void Bullets_OnClick(object sender, RoutedEventArgs e) => ToggleBullets();
    private void Numbering_OnClick(object sender, RoutedEventArgs e) => ToggleNumbering();
    private void Trash_OnClick(object sender, RoutedEventArgs e) => TrashRequested?.Invoke(this, EventArgs.Empty);
    private void New_OnClick(object sender, RoutedEventArgs e) => NewRequested?.Invoke(this, EventArgs.Empty);
    private void List_OnClick(object sender, RoutedEventArgs e) => ListRequested?.Invoke(this, EventArgs.Empty);
    private void Close_OnClick(object sender, RoutedEventArgs e) => RequestClose();
    private void Reconnect_OnClick(object sender, RoutedEventArgs e) => ReconnectRequested?.Invoke(this, EventArgs.Empty);
    private void MoveGrip_OnDragDelta(object sender, DragDeltaEventArgs e) => MoveRequested?.Invoke(this, new Vector(e.HorizontalChange, e.VerticalChange));
    private void ResizeGrip_OnDragDelta(object sender, DragDeltaEventArgs e) => ResizeRequested?.Invoke(this, new Vector(e.HorizontalChange, e.VerticalChange));

    public void SetPaper(string paper)
    {
        if (!NoteTheme.IsPaper(paper)) throw new ArgumentException("지원하지 않는 종이색입니다.", nameof(paper));
        if (Paper == paper) return;
        Paper = paper; PaperBorder.Background = NoteTheme.Paper(paper);
        AppearanceChanged?.Invoke(this, EventArgs.Empty);
    }
    public void SetCollapsed(bool collapsed)
    {
        // Keep the failure message and retry action reachable until a successful save.
        if (collapsed && _saveNeedsAttention) return;
        if (IsCollapsed == collapsed) return;
        IsCollapsed = collapsed;
        Body.Visibility = Footer.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
        AppearanceChanged?.Invoke(this, EventArgs.Empty);
    }
    public void RefreshTitle() => RefreshTitle(new TextRange(Editor.Document.ContentStart, Editor.Document.ContentEnd).Text);
    private void RefreshTitle(string text)
    {
        _textLength = text.Length;
        NoteTitle = Services.NoteTitleSummary.FromText(text);
        Placeholder.Visibility = text.AsSpan().Trim().IsEmpty ? Visibility.Visible : Visibility.Collapsed;
    }
    private void Header_OnMouseDown(object sender, MouseButtonEventArgs e)
    { if (e.ClickCount == 2) { SetCollapsed(!IsCollapsed); e.Handled = true; } }
    private void More_OnClick(object sender, RoutedEventArgs e) => OpenMenu();
    private void OpenMenu()
    {
        if (MoreButton.ContextMenu is not { } menu) return;
        menu.PlacementTarget = MoreButton; menu.Placement = PlacementMode.Bottom; menu.IsOpen = true;
    }
    private void OnViewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.SystemKey == Key.M && Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) { OpenMenu(); e.Handled = true; }
    }
    private void BuildMenu()
    {
        var menu = new ContextMenu { FontFamily = NoteTheme.Font, FontSize = NoteTheme.BodySize };
        MenuItem Item(string text, Action action, string? glyph = null)
        {
            var item = new MenuItem { Header = text, MinHeight = NoteTheme.ToolSize };
            if (glyph is not null) item.Icon = new TextBlock { Text = glyph, FontSize = 16 };
            item.Click += (_, _) => action(); return item;
        }
        menu.Items.Add(Item("새 메모", () => NewRequested?.Invoke(this, EventArgs.Empty), "+"));
        menu.Items.Add(Item("체크리스트 추가 / 제거", ToggleChecklist, "☐"));
        menu.Items.Add(Item("완료 체크 / 해제 (Ctrl+Enter)", ToggleChecklistDone, "☑"));
        var colors = new MenuItem { Header = "종이색", MinHeight = NoteTheme.ToolSize };
        foreach (var (name, value) in new[] { ("버터", "Butter"), ("세이지", "Sage"), ("피치", "Peach") })
        {
            var item = Item(name, () => SetPaper(value));
            item.Icon = new Border { Width = 16, Height = 16, Background = NoteTheme.Paper(value), CornerRadius = new CornerRadius(3) };
            colors.Items.Add(item);
        }
        menu.Items.Add(colors);
        _collapseItem = Item("접기 / 펼치기 (상단 더블클릭)", () => SetCollapsed(!IsCollapsed), "−");
        ToolTipService.SetShowOnDisabled(_collapseItem, true);
        menu.Items.Add(_collapseItem);
        var archive = Item("보관하기", () => ArchiveRequested?.Invoke(this, EventArgs.Empty));
        archive.Icon = new System.Windows.Shapes.Path { Data = Geometry.Parse("M1,3 L15,3 15,6 1,6 Z M3,6 L3,14 13,14 13,6 M6,9 L10,9"), Stroke = NoteTheme.Ink, StrokeThickness = 1.3, Width = 17, Height = 17 };
        menu.Items.Add(archive);
        menu.Items.Add(new Separator());
        var formatting = new MenuItem { Header = "글자 서식", MinHeight = NoteTheme.ToolSize };
        formatting.Items.Add(Item("굵게 (Ctrl+B)", ToggleBold));
        formatting.Items.Add(Item("기본 글자색", () => ApplyColor(NoteTheme.Ink)));
        formatting.Items.Add(Item("빨강", () => ApplyColor(Brushes.Firebrick)));
        formatting.Items.Add(Item("파랑", () => ApplyColor(Brushes.RoyalBlue)));
        formatting.Items.Add(Item("글머리 목록", ToggleBullets));
        formatting.Items.Add(Item("번호 목록", ToggleNumbering));
        menu.Items.Add(formatting);
        menu.Items.Add(Item("메모 목록", () => ListRequested?.Invoke(this, EventArgs.Empty)));
        menu.Items.Add(Item("보관함", () => ArchiveListRequested?.Invoke(this, EventArgs.Empty)));
        menu.Items.Add(Item("숨기기", RequestClose));
        menu.Items.Add(Item("휴지통으로 이동", () => TrashRequested?.Invoke(this, EventArgs.Empty)));
        MoreButton.ContextMenu = menu;
    }
    private static bool Checklist(Paragraph paragraph)
    {
        string prefix = MarkerRange(paragraph, 2).Text;
        return prefix is "☐ " or "☑ ";
    }
    private static TextRange MarkerRange(Paragraph paragraph, int characters)
    {
        var start = paragraph.ContentStart.GetInsertionPosition(LogicalDirection.Forward);
        var end = start;
        for (int i = 0; i < characters; i++) end = end.GetNextInsertionPosition(LogicalDirection.Forward) ?? paragraph.ContentEnd;
        return new TextRange(start, end);
    }
    public void ToggleChecklist()
    {
        var paragraph = Editor.CaretPosition.Paragraph;
        if (paragraph is null) return;
        Editor.BeginChange();
        try
        {
            if (Checklist(paragraph))
            {
                if (paragraph.Inlines.FirstInline is Run run && (run.Text.StartsWith("☐ ", StringComparison.Ordinal) || run.Text.StartsWith("☑ ", StringComparison.Ordinal)))
                    run.Text = run.Text[2..];
                else MarkerRange(paragraph, 2).Text = "";
            }
            else if (CanInsert(2))
            {
                if (paragraph.Inlines.FirstInline is { } first) paragraph.Inlines.InsertBefore(first, new Run("☐ "));
                else paragraph.Inlines.Add(new Run("☐ "));
            }
        }
        finally { Editor.EndChange(); }
        Editor.Focus();
    }
    public void ToggleChecklistDone()
    {
        var paragraph = Editor.CaretPosition.Paragraph;
        if (paragraph is null || !Checklist(paragraph)) return;
        if (paragraph.Inlines.FirstInline is Run run && run.Text.Length > 0)
            run.Text = (run.Text[0] == '☑' ? "☐" : "☑") + run.Text[1..];
        else
        {
            var marker = MarkerRange(paragraph, 1);
            marker.Text = marker.Text == "☑" ? "☐" : "☑";
        }
        Editor.Focus();
    }
    private void Editor_OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control) { ToggleChecklistDone(); e.Handled = true; }
        else if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None && Editor.Selection.IsEmpty
            && Editor.CaretPosition.Paragraph is { } paragraph && Checklist(paragraph))
        {
            if (!CanInsert(4)) { e.Handled = true; return; }
            Editor.BeginChange();
            try { EditingCommands.EnterParagraphBreak.Execute(null, Editor); ToggleChecklist(); }
            finally { Editor.EndChange(); }
            e.Handled = true;
        }
    }
    private void Editor_OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        var point = e.GetPosition(Editor);
        var pointer = Editor.GetPositionFromPoint(point, false);
        if (pointer?.Paragraph is not { } paragraph || !Checklist(paragraph)) return;
        var start = paragraph.ContentStart.GetInsertionPosition(LogicalDirection.Forward);
        var rect = start.GetCharacterRect(LogicalDirection.Forward);
        double right = MarkerRange(paragraph, 1).End.GetCharacterRect(LogicalDirection.Backward).Right + 2;
        if (point.X >= rect.Left && point.X <= right && point.Y >= rect.Top && point.Y <= rect.Bottom)
        { Editor.CaretPosition = start; ToggleChecklistDone(); e.Handled = true; }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        DataObject.RemovePastingHandler(Editor, OnPasting);
        PreviewKeyDown -= OnViewKeyDown;
        if (MoreButton.ContextMenu is { } menu) { menu.IsOpen = false; menu.Items.Clear(); MoreButton.ContextMenu = null; }
        _collapseItem = null;
        AppearanceChanged = null;
        ArchiveRequested = null;
        ArchiveListRequested = null;
        DocumentChanged = null;
        SaveRequested = null;
        CloseRequested = null;
        NewRequested = null;
        TrashRequested = null;
        ListRequested = null;
        ReconnectRequested = null;
        MoveRequested = null;
        ResizeRequested = null;
    }
}
