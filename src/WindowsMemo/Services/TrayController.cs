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
    private readonly StartupService? _startupService;
    private readonly UpdaterService? _updater;
    private readonly Forms.ToolStripMenuItem _update = new("업데이트 확인 (로컬 시험)");
    private readonly Forms.ToolStripMenuItem _startup = new("로그인 시 자동 실행") { CheckOnClick = false, ToolTipText = "앱의 등록 상태입니다. Windows 시작 앱 설정이나 정책에서 별도로 차단할 수 있습니다." };
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

    public TrayController(Application app, NoteWorkspace workspace, StartupService? startupService, UpdaterService? updater = null)
    {
        _app = app; _workspace = workspace; _startupService = startupService; _updater = updater;
        try
        {
            _menu.Items.AddRange([_new, _list, _trash, new Forms.ToolStripSeparator(), _startup, new Forms.ToolStripSeparator(), _exit]);
            if (_updater is not null) _menu.Items.Insert(_menu.Items.Count - 1, _update);
            _tray.Icon = _icon; _tray.Text = $"Windows 메모 · {typeof(TrayController).Assembly.GetName().Version?.ToString(3)} prototype"; _tray.ContextMenuStrip = _menu;
            _new.Click += OnNew; _list.Click += OnList; _trash.Click += OnTrash; _exit.Click += OnExit;
            _tray.DoubleClick += OnList;
            _startup.Click += OnStartup;
            _update.Click += OnUpdate;
            _menu.Opening += OnMenuOpening;
            RefreshStartup();
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
    private void OnMenuOpening(object? sender, System.ComponentModel.CancelEventArgs e)
    { RefreshStartup(); RefreshUpdate(); }
    private void RefreshUpdate()
    {
        _update.Enabled = _updater is not null && !_updater.IsBusy && !_workspace.IsBusy && !_exiting;
        _update.Text = _updater?.State switch
        {
            UpdateState.Checking => "업데이트 확인·다운로드 중…",
            UpdateState.Ready => $"저장 후 {_updater.Version} 업데이트 (로컬 시험)",
            UpdateState.Applying => "저장 후 업데이트 적용 중…",
            _ => "업데이트 확인 (로컬 시험)"
        };
    }
    private void OnUpdate(object? sender, EventArgs e) => Dispatch(async () =>
    {
        if (_updater is null || _updater.IsBusy || _workspace.IsBusy || _exiting) return;
        if (_updater.State == UpdateState.Ready)
        {
            if (MessageBox.Show("모든 메모를 저장한 후 업데이트하고 다시 시작할까요?", "로컬 시험 업데이트",
                MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
            var apply = _updater.ApplyAsync(_workspace.SaveThenAsync);
            RefreshUpdate();
            if (!await apply && !_disposed)
                MessageBox.Show("저장 또는 업데이트 적용에 실패했습니다. 메모를 유지하고 있으니 다시 시도하세요.", "업데이트 보류");
        }
        else
        {
            var check = _updater.CheckAsync();
            RefreshUpdate();
            await check;
            if (!_disposed) MessageBox.Show(_updater.State switch
            {
                UpdateState.Ready => "업데이트 다운로드가 완료되었습니다. 트레이에서 저장 후 업데이트를 선택하세요.",
                UpdateState.Failed => "업데이트를 가져오지 못했습니다. 시험 폴더와 패키지를 확인한 후 다시 시도하세요. 현재 메모는 계속 사용할 수 있습니다.",
                _ => "새 업데이트가 없습니다."
            }, "로컬 시험 업데이트");
        }
        if (!_disposed) RefreshUpdate();
    });
    private void RefreshStartup()
    {
        _startup.Enabled = _startupService is not null;
        if (_startupService is null)
        {
            _startup.Checked = false;
            _startup.Text = "로그인 시 자동 실행 (일반 창 진단에서는 변경 불가)";
            return;
        }
        try
        {
            var state = _startupService.GetState();
            _startup.Checked = state == StartupState.Enabled;
            _startup.Text = state == StartupState.OtherLocation ? "로그인 시 자동 실행 (다른 경로 등록됨 · 클릭하여 해제)" : "로그인 시 자동 실행";
        }
        catch (Exception ex) when (StartupService.IsStartupError(ex))
        {
            _startup.Checked = false;
            _startup.Text = "로그인 시 자동 실행 (등록 확인 실패)";
        }
    }
    private void OnStartup(object? sender, EventArgs e) => Dispatch(() =>
    {
        if (_startupService is null) return;
        try
        {
            bool enable = _startupService.GetState() == StartupState.Disabled;
            if (enable && MessageBox.Show("현재 실행 파일을 로그인 시 자동으로 실행할까요? 실행 폴더를 이동하거나 삭제하면 다시 등록해야 합니다.",
                "로그인 시 자동 실행", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
            _startupService.SetEnabled(enable);
        }
        catch (Exception ex) when (StartupService.IsStartupError(ex))
        { MessageBox.Show("자동 실행 설정을 변경하지 못했습니다. 등록 상태를 다시 확인해 주세요.\n" + ex.Message, "자동 실행 설정 실패", MessageBoxButton.OK, MessageBoxImage.Warning); }
        finally { RefreshStartup(); }
    });
    private void OnChanged(object? sender, EventArgs e)
    { _new.Enabled = !_workspace.IsBusy; _exit.Enabled = !_workspace.IsBusy; RefreshUpdate(); }
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
        _startup.Click -= OnStartup; _menu.Opening -= OnMenuOpening;
        _update.Click -= OnUpdate;
        _update.Dispose();
        _tray.Visible = false; _tray.ContextMenuStrip = null;
        _tray.Dispose(); _menu.Dispose(); _icon.Dispose();
        _window?.Close(); _window = null;
        _trashWindow?.Close(); _trashWindow = null;
    }
}
