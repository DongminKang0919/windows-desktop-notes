using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using WindowsMemo.Views;
using WindowsMemo.Models;

namespace WindowsMemo.Interop;

/// <summary>Owns only the note surfaces. Explorer windows are never destroyed by this class.</summary>
public sealed class DesktopNoteHost : IDisposable
{
    internal const int MaxRecoveryAttempts = 10;
    private readonly NoteView _view;
    private readonly Func<nint> _desktopLocator;
    private readonly bool _watchChanges;
    private readonly DispatcherTimer _timer;
    private HwndSource? _source;
    private HwndSource? _observer;
    private Window? _fallback;
    private nint _parent;
    private PixelBounds _bounds;
    private NativeMethods.Rect _lastClient;
    private double _scale = 1;
    private bool _visible, _opened, _disposed, _checking, _checkQueued, _closingFallback;
    private uint _taskbarCreated;
    public nint Handle => _source is { IsDisposed: false } ? _source.Handle : 0;
    internal bool HasFallback => _fallback is not null;
    internal int RecoveryAttempts { get; private set; }
    internal bool RecoveryStopped => RecoveryAttempts >= MaxRecoveryAttempts;

    public DesktopNoteHost(NoteView view) : this(view, FindDesktopView, true) { }

    internal DesktopNoteHost(NoteView view, Func<nint> desktopLocator, bool watchChanges)
    {
        _view = view;
        _desktopLocator = desktopLocator;
        _watchChanges = watchChanges;
        _timer = new DispatcherTimer(DispatcherPriority.Background, view.Dispatcher)
        {
            Interval = TimeSpan.FromSeconds(2)
        };
        _timer.Tick += TimerTick;
    }

    public event EventHandler? BoundsChanged;
    public NoteBounds CaptureBounds() => new(_bounds.X, _bounds.Y, _bounds.Width / _scale, _bounds.Height / _scale, _scale);

    public void Open(bool visible = true, NoteBounds? restoredBounds = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_opened) throw new InvalidOperationException("메모 창은 이미 열려 있습니다.");
        _visible = visible;
        _parent = _desktopLocator();
        if (_parent == 0 || !NativeMethods.IsWindow(_parent))
            throw new InvalidOperationException("Windows 바탕화면 창을 찾지 못했습니다.");
        _scale = GetScale(_parent);
        _bounds = restoredBounds is null
            ? new PixelBounds(48, 64, (int)(420 * _scale), (int)(480 * _scale))
            : new PixelBounds((int)restoredBounds.X, (int)restoredBounds.Y, (int)(restoredBounds.Width * _scale), (int)(restoredBounds.Height * _scale));
        try
        {
            AttachSurface();
            _view.MoveRequested += OnMove;
            _view.ResizeRequested += OnResize;
            _view.ReconnectRequested += OnReconnect;
            _opened = true;
            if (_watchChanges)
            {
                _taskbarCreated = NativeMethods.RegisterWindowMessage("TaskbarCreated");
                _observer = new HwndSource(new HwndSourceParameters("WindowsMemo.DisplayObserver", 1, 1)
                {
                    WindowStyle = unchecked((int)0x80000000), HwndSourceHook = ObserverHook
                });
                _timer.Start();
            }
        }
        catch { Dispose(); throw; }
    }

    public static nint FindDesktopView()
    {
        nint shell = NativeMethods.GetShellWindow();
        if (shell == 0) return 0;
        NativeMethods.GetWindowThreadProcessId(shell, out uint shellProcess);
        nint result = 0;
        NativeMethods.EnumWindows((window, _) =>
        {
            NativeMethods.GetWindowThreadProcessId(window, out uint process);
            if (process != shellProcess) return true;
            nint desktop = NativeMethods.FindWindowEx(window, 0, "SHELLDLL_DefView", null);
            if (desktop == 0 || !NativeMethods.IsWindowVisible(desktop)) return true;
            result = desktop;
            return false;
        }, 0);
        return result;
    }

    private void AttachSurface()
    {
        FitToDesktop();
        var origin = ParentOrigin();
        var parameters = new HwndSourceParameters("Windows 메모 · 바탕화면 prototype", _bounds.Width, _bounds.Height)
        {
            ParentWindow = _parent,
            PositionX = _bounds.X - origin.X, PositionY = _bounds.Y - origin.Y,
            WindowStyle = NativeMethods.WsChild | NativeMethods.WsClipSiblings | (_visible ? NativeMethods.WsVisible : 0),
            TreatAsInputRoot = true, HwndSourceHook = WindowHook
        };
        if (_fallback is not null) _fallback.Content = null;
        try
        {
            _source = new HwndSource(parameters);
            _source.RootVisual = _view;
            ApplyBounds();
            CloseFallback();
            RecoveryAttempts = 0;
            _view.SetRecoveryMode("바탕화면 연결됨 · Win+D로 확인", false);
        }
        catch
        {
            ReleaseSurface();
            if (_fallback is not null) _fallback.Content = _view;
            throw;
        }
    }

    private nint WindowHook(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == 0x0021) // WM_MOUSEACTIVATE
        {
            NativeMethods.SetForegroundWindow(NativeMethods.GetAncestor(hwnd, NativeMethods.GaRoot));
            NativeMethods.SetFocus(hwnd);
            handled = true;
            return 1;
        }
        if (message is 0x02E0 or 0x02E3) QueueCheck(); // DPI changed / after parent
        return 0;
    }

    private nint ObserverHook(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message is 0x007E or 0x001A or 0x0218 || (_taskbarCreated != 0 && message == _taskbarCreated))
            QueueCheck(); // display/settings/power change, Explorer recreated
        return 0;
    }

    private void QueueCheck()
    {
        if (_disposed || _checkQueued) return;
        _checkQueued = true;
        _view.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            _checkQueued = false;
            if (!_disposed) CheckConnection(refreshLayout: true);
        }));
    }

    private void TimerTick(object? sender, EventArgs e) => CheckConnection();

    internal void CheckConnection(bool refreshLayout = false)
    {
        if (_disposed || !_opened || _checking || RecoveryStopped) return;
        _checking = true;
        try
        {
            bool alive = OwnsSurface() && NativeMethods.IsWindow(_parent)
                && NativeMethods.GetParent(Handle) == _parent;
            if (alive)
            {
                if (!NativeMethods.GetClientRect(_parent, out var client))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                if (refreshLayout || !client.Equals(_lastClient))
                {
                    double scale = GetScale(Handle);
                    _bounds = _bounds.ScaleSize(_scale, scale);
                    _scale = scale;
                    FitToDesktop();
                    ApplyBounds();
                }
                return;
            }
            ReleaseSurface();
            ShowFallback();
            RecoveryAttempts++;
            _parent = _desktopLocator();
            if (_parent != 0 && NativeMethods.IsWindow(_parent))
            {
                double scale = GetScale(_parent);
                _bounds = _bounds.ScaleSize(_scale, scale);
                _scale = scale;
                AttachSurface();
                return;
            }
            UpdateRecoveryNotice();
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            ReleaseSurface();
            ShowFallback();
            if (RecoveryAttempts == 0) RecoveryAttempts = 1;
            UpdateRecoveryNotice();
        }
        finally { _checking = false; }
    }

    private void UpdateRecoveryNotice()
    {
        if (RecoveryStopped) _timer.Stop();
        _view.SetRecoveryMode(RecoveryStopped
            ? "바탕화면 연결을 복구하지 못했습니다. 내용을 유지한 임시 창입니다."
            : $"바탕화면 연결 복구 중 ({RecoveryAttempts}/{MaxRecoveryAttempts}) · 임시 창에서 계속 편집할 수 있습니다.", true);
    }

    private void OnReconnect(object? sender, EventArgs e) => RetryConnection();
    internal void RetryConnection()
    {
        if (_disposed || !_opened) return;
        RecoveryAttempts = 0;
        if (_watchChanges) _timer.Start();
        CheckConnection(refreshLayout: true);
    }

    private void ShowFallback()
    {
        if (_fallback is not null) return;
        _fallback = new Window
        {
            Title = "Windows 메모 · 연결 복구", Width = 420, Height = 480,
            MinWidth = 320, MinHeight = 260, Content = _view,
            WindowStartupLocation = WindowStartupLocation.CenterScreen
        };
        _fallback.Closing += FallbackClosing;
        if (_visible) _fallback.Show();
    }

    private void FallbackClosing(object? sender, CancelEventArgs e)
    {
        if (_closingFallback || _disposed) return;
        e.Cancel = true;
        _view.RequestClose();
    }

    private void CloseFallback()
    {
        if (_fallback is null) return;
        _closingFallback = true;
        try
        {
            _fallback.Closing -= FallbackClosing;
            _fallback.Content = null;
            _fallback.Close();
            _fallback = null;
        }
        finally { _closingFallback = false; }
    }

    private void OnMove(object? sender, Vector delta)
    {
        if (Handle == 0) return;
        double scale = GetScale(Handle);
        _bounds = _bounds with { X = _bounds.X + (int)Math.Round(delta.X * scale), Y = _bounds.Y + (int)Math.Round(delta.Y * scale) };
        UpdateBounds(followCursor: true);
    }
    private void OnResize(object? sender, Vector delta)
    {
        if (Handle == 0) return;
        double scale = GetScale(Handle);
        _bounds = _bounds with { Width = _bounds.Width + (int)Math.Round(delta.X * scale), Height = _bounds.Height + (int)Math.Round(delta.Y * scale) };
        UpdateBounds();
    }
    private void UpdateBounds(bool followCursor = false)
    {
        try { FitToDesktop(followCursor); ApplyBounds(); }
        catch (Win32Exception) { QueueCheck(); }
    }

    private NativeMethods.Point ParentOrigin()
    {
        var point = new NativeMethods.Point();
        if (!NativeMethods.ClientToScreen(_parent, ref point)) throw new Win32Exception(Marshal.GetLastWin32Error());
        return point;
    }

    private void FitToDesktop(bool followCursor = false)
    {
        if (!NativeMethods.GetClientRect(_parent, out _lastClient)) throw new Win32Exception(Marshal.GetLastWin32Error());
        var origin = ParentOrigin();
        var rect = new NativeMethods.Rect { Left = _bounds.X, Top = _bounds.Y, Right = _bounds.X + _bounds.Width, Bottom = _bounds.Y + _bounds.Height };
        nint monitor = followCursor && NativeMethods.GetCursorPos(out var cursor)
            ? NativeMethods.MonitorFromPoint(cursor, NativeMethods.MonitorDefaultToNearest)
            : NativeMethods.MonitorFromRect(ref rect, NativeMethods.MonitorDefaultToNearest);
        var info = new NativeMethods.MonitorInfo { Size = Marshal.SizeOf<NativeMethods.MonitorInfo>() };
        if (monitor == 0 || !NativeMethods.GetMonitorInfo(monitor, ref info)) throw new Win32Exception(Marshal.GetLastWin32Error());
        int left = Math.Max(origin.X, info.Work.Left), top = Math.Max(origin.Y, info.Work.Top);
        int right = Math.Min(origin.X + _lastClient.Right, info.Work.Right), bottom = Math.Min(origin.Y + _lastClient.Bottom, info.Work.Bottom);
        var area = right > left && bottom > top
            ? new PixelBounds(left, top, right - left, bottom - top)
            : new PixelBounds(origin.X, origin.Y, Math.Max(1, _lastClient.Right), Math.Max(1, _lastClient.Bottom));
        _bounds = _bounds.FitWithin(area, (int)(320 * _scale), (int)(260 * _scale));
    }

    private void ApplyBounds()
    {
        if (!NativeMethods.IsWindow(_parent) || !OwnsSurface()) throw new Win32Exception("바탕화면 창이 더 이상 유효하지 않습니다.");
        var origin = ParentOrigin();
        if (!NativeMethods.SetWindowPos(Handle, 0, _bounds.X - origin.X, _bounds.Y - origin.Y, _bounds.Width, _bounds.Height, NativeMethods.SwpNoActivate))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        BoundsChanged?.Invoke(this, EventArgs.Empty);
    }

    private static double GetScale(nint hwnd) => Math.Max(96, NativeMethods.GetDpiForWindow(hwnd)) / 96.0;

    private bool OwnsSurface()
    {
        nint handle = Handle;
        if (handle == 0 || !NativeMethods.IsWindow(handle)) return false;
        NativeMethods.GetWindowThreadProcessId(handle, out uint process);
        return process == Environment.ProcessId;
    }

    private void ReleaseSurface()
    {
        if (_source is null) return;
        if (!_source.IsDisposed)
        {
            _source.RemoveHook(WindowHook);
            _source.RootVisual = null;
            _source.Dispose();
        }
        _source = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        BoundsChanged = null;
        _timer.Stop();
        _timer.Tick -= TimerTick;
        _view.MoveRequested -= OnMove;
        _view.ResizeRequested -= OnResize;
        _view.ReconnectRequested -= OnReconnect;
        CloseFallback();
        ReleaseSurface();
        if (_observer is not null)
        {
            _observer.RemoveHook(ObserverHook);
            _observer.Dispose();
            _observer = null;
        }
    }
}
