using System.Drawing;
using System.Windows;
using System.Windows.Threading;
using WindowsMemo.Views;
using Forms = System.Windows.Forms;

namespace WindowsMemo.Services;

internal sealed class TrayController : IDisposable
{
    private readonly Application _app;
    private readonly NoteWorkspace _workspace;
    private readonly Forms.ContextMenuStrip _menu = new();
    private readonly Forms.NotifyIcon _tray = new();
    private readonly Icon _icon = (Icon)SystemIcons.Application.Clone();
    private readonly Forms.ToolStripMenuItem _new = new("새 메모");
    private readonly Forms.ToolStripMenuItem _list = new("메모 목록");
    private readonly Forms.ToolStripMenuItem _trash = new("휴지통");
    private NoteListWindow? _trashWindow;
    private readonly Forms.ToolStripMenuItem _exit = new("모든 메모 저장 후 종료");
    private NoteListWindow? _window;
    private bool _disposed, _exiting;

    public TrayController(Application app, NoteWorkspace workspace)
    {
        _app = app; _workspace = workspace;
        try
        {
            _menu.Items.AddRange([_new, _list, _trash, new Forms.ToolStripSeparator(), _exit]);
            _tray.Icon = _icon; _tray.Text = $"Windows 메모 · {typeof(TrayController).Assembly.GetName().Version?.ToString(3)} prototype"; _tray.ContextMenuStrip = _menu;
            _new.Click += OnNew; _list.Click += OnList; _trash.Click += OnTrash; _exit.Click += OnExit;
            _tray.DoubleClick += OnList;
            workspace.Changed += OnChanged; workspace.ListRequested += OnList; workspace.Notice += OnNotice;
            _tray.Visible = true;
        }
        catch { Dispose(); throw; }
    }
    private void Dispatch(Action action) => _app.Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() => { if (!_disposed) action(); }));
    private void OnNew(object? sender, EventArgs e) => Dispatch(() =>
    {
        try { _workspace.Create(); }
        catch (Exception ex) when (NoteWorkspace.IsNoteError(ex)) { OnNotice(this, "새 메모를 열지 못했습니다. 기존 내용은 유지됩니다."); }
    });
    private void OnList(object? sender, EventArgs e) => Dispatch(ShowList);
    public void ShowList()
    {
        if (_disposed) return;
        if (_window is null)
        {
            _window = new NoteListWindow(_workspace);
            _window.Closed += (_, _) => _window = null;
            _window.Show();
        }
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
    }
    private void OnTrash(object? sender, EventArgs e) => Dispatch(() =>
    {
        if (_trashWindow is null)
        {
            _trashWindow = new NoteListWindow(_workspace, trash: true);
            _trashWindow.Closed += (_, _) => _trashWindow = null;
            _trashWindow.Show();
        }
        if (_trashWindow.WindowState == WindowState.Minimized) _trashWindow.WindowState = WindowState.Normal;
        _trashWindow.Activate();
    });
    private void OnChanged(object? sender, EventArgs e)
    { _new.Enabled = !_workspace.IsBusy; _exit.Enabled = !_workspace.IsBusy; }
    private void OnNotice(object? sender, string text) => MessageBox.Show(text, "Windows 메모", MessageBoxButton.OK, MessageBoxImage.Information);
    private void OnExit(object? sender, EventArgs e) => Dispatch(async () => await ExitAsync());
    public async Task ExitAsync()
    {
        if (_exiting || _workspace.IsBusy) return;
        _exiting = true;
        try
        {
            if (await _workspace.FlushAllAsync()) _app.Shutdown();
            else MessageBox.Show("저장하지 못해 종료를 취소했습니다. 모든 메모를 유지하고 있으니 저장을 다시 시도하세요.", "저장 실패", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { _exiting = false; }
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _workspace.Changed -= OnChanged; _workspace.ListRequested -= OnList; _workspace.Notice -= OnNotice;
        _new.Click -= OnNew; _list.Click -= OnList; _trash.Click -= OnTrash; _exit.Click -= OnExit; _tray.DoubleClick -= OnList;
        _tray.Visible = false; _tray.ContextMenuStrip = null;
        _tray.Dispose(); _menu.Dispose(); _icon.Dispose();
        _window?.Close(); _window = null;
        _trashWindow?.Close(); _trashWindow = null;
    }
}
