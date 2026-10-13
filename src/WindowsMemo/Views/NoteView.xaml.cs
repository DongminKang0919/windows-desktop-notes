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
    public event EventHandler? DocumentChanged;
    public event EventHandler? SaveRequested;
    public event EventHandler? CloseRequested;
    public event EventHandler? ReconnectRequested;
    public event EventHandler<Vector>? MoveRequested;
    public event EventHandler<Vector>? ResizeRequested;
    public RichTextBox TextEditor => Editor;

    public NoteView()
    {
        InitializeComponent();
        DataObject.AddPastingHandler(Editor, OnPasting);
    }

    public void SetSaveStatus(string text, bool canRetry)
    {
        SaveLabel.Text = text;
        SaveButton.Visibility = canRetry ? Visibility.Visible : Visibility.Collapsed;
    }
    private void Save_OnClick(object sender, RoutedEventArgs e) => SaveRequested?.Invoke(this, EventArgs.Empty);

    public void SetMode(string text) => ModeLabel.Text = text;
    public void SetRecoveryMode(string text, bool canRetry)
    {
        SetMode(text);
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
        new TextRange(Editor.Document.ContentStart, Editor.Document.ContentEnd).Text.Length
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
        if (new TextRange(Editor.Document.ContentStart, Editor.Document.ContentEnd).Text.Length > MaxCharacters && Editor.CanUndo)
        {
            _rollingBack = true;
            try { Editor.Undo(); }
            finally { _rollingBack = false; }
            NoticeLabel.Text = "입력 한도를 초과하여 마지막 변경을 취소했습니다.";
        }
        else NoticeLabel.Text = "외부 붙여넣기는 텍스트만 허용합니다.";
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
        if (command.CanExecute(null, Editor)) command.Execute(null, Editor);
        Editor.Focus();
    }
    private void Bold_OnClick(object sender, RoutedEventArgs e) => ToggleBold();
    private void Black_OnClick(object sender, RoutedEventArgs e) => ApplyColor(Brushes.Black);
    private void Red_OnClick(object sender, RoutedEventArgs e) => ApplyColor(Brushes.Firebrick);
    private void Blue_OnClick(object sender, RoutedEventArgs e) => ApplyColor(Brushes.RoyalBlue);
    private void Bullets_OnClick(object sender, RoutedEventArgs e) => ToggleBullets();
    private void Numbering_OnClick(object sender, RoutedEventArgs e) => ToggleNumbering();
    private void Close_OnClick(object sender, RoutedEventArgs e) => RequestClose();
    private void Reconnect_OnClick(object sender, RoutedEventArgs e) => ReconnectRequested?.Invoke(this, EventArgs.Empty);
    private void MoveGrip_OnDragDelta(object sender, DragDeltaEventArgs e) => MoveRequested?.Invoke(this, new Vector(e.HorizontalChange, e.VerticalChange));
    private void ResizeGrip_OnDragDelta(object sender, DragDeltaEventArgs e) => ResizeRequested?.Invoke(this, new Vector(e.HorizontalChange, e.VerticalChange));

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        DataObject.RemovePastingHandler(Editor, OnPasting);
        DocumentChanged = null;
        SaveRequested = null;
        CloseRequested = null;
        ReconnectRequested = null;
        MoveRequested = null;
        ResizeRequested = null;
    }
}
