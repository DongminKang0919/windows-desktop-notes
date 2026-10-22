using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
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
    private bool _visible, _opened, _disposed, _checking, _checkQueued, _resumeQueued, _closingFallback;
    private bool _updatingFallback, _fallbackCollapsed;
    private uint _taskbarCreated;
    private (int Width, int Height, int Radius)? _regionShape;
    private NoteBounds? _publishedBounds;
    public nint Handle => _source is { IsDisposed: false } ? _source.Handle : 0;
    internal bool HasFallback => _fallback is not null;
    internal nint FallbackHandle => _fallback is null ? 0 : new WindowInteropHelper(_fallback).Handle;
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
        bool desktopReady = _parent != 0 && NativeMethods.IsWindow(_parent);
        // Keep saved geometry usable for autosave while Explorer is still starting.
        _scale = desktopReady ? GetScale(_parent) : restoredBounds?.Scale ?? 1;
        _bounds = restoredBounds is null
            ? new PixelBounds(48, 64, (int)(NoteTheme.Width * _scale), (int)(NoteTheme.Height * _scale))
            : new PixelBounds((int)restoredBounds.X, (int)restoredBounds.Y, (int)(restoredBounds.Width * _scale), (int)(restoredBounds.Height * _scale));
        try
        {
            _view.MoveRequested += OnMove;
            _view.ResizeRequested += OnResize;
            _view.ReconnectRequested += OnReconnect;
            _view.AppearanceChanged += OnAppearance;
            _opened = true;
            if (desktopReady) AttachSurface();
            else
            {
                ShowFallback();
                UpdateRecoveryNotice();
            }
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
            _source.CompositionTarget.BackgroundColor = ((SolidColorBrush)NoteTheme.Paper(_view.Paper)).Color;
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
        bool shellCreated = _taskbarCreated != 0 && message == _taskbarCreated;
        bool resumed = message == 0x0218 && (wParam == 0x12 || wParam == 7); // automatic / user resume
        if (message is 0x007E or 0x001A or 0x0218 || shellCreated)
            QueueCheck(resumeRecovery: shellCreated || resumed);
        if (resumed)
        {
            handled = true;
            return 1; // WM_POWERBROADCAST: TRUE when processed; never dereference lParam.
        }
        return 0;
    }

    private void QueueCheck(bool resumeRecovery = false)
    {
        if (_disposed) return;
        // A resume signal must survive coalescing behind an ordinary layout notification.
        _resumeQueued |= resumeRecovery;
        if (_checkQueued) return;
        _checkQueued = true;
        _view.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            _checkQueued = false;
            bool resume = _resumeQueued;
            _resumeQueued = false;
            if (_disposed) return;
            if (FallbackHandle != 0) ApplyFallbackBounds();
            // Only an explicit shell/resume signal re-arms an exhausted retry budget.
            // Settings/battery changes cannot create an endless polling loop.
            if (resume && RecoveryStopped) RetryConnection();
            else CheckConnection(refreshLayout: true);
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
            : RecoveryAttempts == 0
                ? "바탕화면 준비를 기다리고 있습니다. 임시 창에서 계속 편집할 수 있습니다."
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
        _fallbackCollapsed = _view.IsCollapsed;
        _fallback = new Window
        {
            Title = "Windows 메모 · 연결 복구", Width = _bounds.Width / _scale,
            Height = _view.IsCollapsed ? NoteTheme.CollapsedHeight : _bounds.Height / _scale,
            MinWidth = 260, MinHeight = NoteTheme.CollapsedHeight, Content = _view,
            WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
            Background = NoteTheme.Paper(_view.Paper), WindowStartupLocation = WindowStartupLocation.Manual
        };
        _fallback.Closing += FallbackClosing;
        _fallback.LocationChanged += FallbackBoundsChanged;
        _fallback.SizeChanged += FallbackBoundsChanged;
        _fallback.StateChanged += FallbackStateChanged;
        if (_visible)
        {
            // Show can update WPF's initial placement; ignore it until saved physical bounds are applied.
            _updatingFallback = true;
            try { _fallback.Show(); }
            finally { _updatingFallback = false; }
            ApplyFallbackBounds();
        }
    }

    private void FallbackBoundsChanged(object? sender, EventArgs e)
    {
        if (_updatingFallback || _closingFallback || _disposed || _fallback?.WindowState != WindowState.Normal) return;
        ReadFallbackBounds();
    }

    private void ReadFallbackBounds()
    {
        if (_fallback?.WindowState != WindowState.Normal) return;
        nint handle = FallbackHandle;
        if (handle == 0 || !NativeMethods.GetWindowRect(handle, out var rect)) return;
        if (rect.Right <= rect.Left || rect.Bottom <= rect.Top) return;
        double scale = GetScale(handle);
        int expandedHeight = _fallbackCollapsed ? (int)Math.Round(_bounds.Height / _scale * scale) : rect.Bottom - rect.Top;
        _bounds = new PixelBounds(rect.Left, rect.Top, rect.Right - rect.Left, expandedHeight);
        _scale = scale;
        PublishBounds();
    }

    private void ApplyFallbackBounds(Vector move = default, Vector resize = default, bool followCursor = false)
    {
        if (_fallback is null || _disposed || _fallback.WindowState != WindowState.Normal) return;
        _updatingFallback = true;
        try
        {
            nint handle = new WindowInteropHelper(_fallback).EnsureHandle();
            double scale = GetScale(handle);
            _bounds = _bounds.ScaleSize(_scale, scale);
            _scale = scale;
            _bounds = new PixelBounds(_bounds.X + (int)Math.Round(move.X * scale), _bounds.Y + (int)Math.Round(move.Y * scale),
                _bounds.Width + (int)Math.Round(resize.X * scale), _bounds.Height + (int)Math.Round(resize.Y * scale));
            _bounds = _bounds.FitWithin(MonitorWorkArea(followCursor), (int)(260 * scale), (int)(200 * scale));
            int height = _view.IsCollapsed ? (int)(NoteTheme.CollapsedHeight * scale) : _bounds.Height;
            if (!NativeMethods.SetWindowPos(handle, 0, _bounds.X, _bounds.Y, _bounds.Width, height,
                NativeMethods.SwpNoActivate | NativeMethods.SwpNoZOrder)) throw new Win32Exception(Marshal.GetLastWin32Error());
            _fallbackCollapsed = _view.IsCollapsed;
        }
        finally { _updatingFallback = false; }
        ReadFallbackBounds();
    }

    private void FallbackStateChanged(object? sender, EventArgs e)
    {
        // Minimized window rectangles are not note positions. Reapply any appearance change on restore.
        if (!_disposed && !_closingFallback && _fallback?.WindowState == WindowState.Normal) QueueCheck();
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
            _fallback.LocationChanged -= FallbackBoundsChanged;
            _fallback.SizeChanged -= FallbackBoundsChanged;
            _fallback.StateChanged -= FallbackStateChanged;
            _fallback.Content = null;
            _fallback.Close();
            _fallback = null;
        }
        finally { _closingFallback = false; }
    }

    private void OnMove(object? sender, Vector delta)
    {
        if (Handle == 0) { ApplyFallbackBounds(move: delta, followCursor: true); return; }
        double scale = GetScale(Handle);
        _bounds = _bounds with { X = _bounds.X + (int)Math.Round(delta.X * scale), Y = _bounds.Y + (int)Math.Round(delta.Y * scale) };
        UpdateBounds(followCursor: true);
    }
    private void OnResize(object? sender, Vector delta)
    {
        if (_view.IsCollapsed) return;
        if (Handle == 0) { ApplyFallbackBounds(resize: delta); return; }
        double scale = GetScale(Handle);
        _bounds = _bounds with { Width = _bounds.Width + (int)Math.Round(delta.X * scale), Height = _bounds.Height + (int)Math.Round(delta.Y * scale) };
        UpdateBounds();
    }
    private void OnAppearance(object? sender, EventArgs e)
    {
        if (_source is { IsDisposed: false }) _source.CompositionTarget.BackgroundColor = ((SolidColorBrush)NoteTheme.Paper(_view.Paper)).Color;
        if (_fallback is not null)
        {
            _fallback.Background = NoteTheme.Paper(_view.Paper);
            ApplyFallbackBounds();
        }
        if (Handle != 0) UpdateBounds();
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
        var work = MonitorWorkArea(followCursor);
        int left = Math.Max(origin.X, work.X), top = Math.Max(origin.Y, work.Y);
        int right = Math.Min(origin.X + _lastClient.Right, work.X + work.Width), bottom = Math.Min(origin.Y + _lastClient.Bottom, work.Y + work.Height);
        var area = right > left && bottom > top
            ? new PixelBounds(left, top, right - left, bottom - top)
            : new PixelBounds(origin.X, origin.Y, Math.Max(1, _lastClient.Right), Math.Max(1, _lastClient.Bottom));
        _bounds = _bounds.FitWithin(area, (int)(260 * _scale), (int)(200 * _scale));
    }

    private PixelBounds MonitorWorkArea(bool followCursor)
    {
        var rect = new NativeMethods.Rect { Left = _bounds.X, Top = _bounds.Y, Right = _bounds.X + _bounds.Width, Bottom = _bounds.Y + _bounds.Height };
        nint monitor = followCursor && NativeMethods.GetCursorPos(out var cursor)
            ? NativeMethods.MonitorFromPoint(cursor, NativeMethods.MonitorDefaultToNearest)
            : NativeMethods.MonitorFromRect(ref rect, NativeMethods.MonitorDefaultToNearest);
        var info = new NativeMethods.MonitorInfo { Size = Marshal.SizeOf<NativeMethods.MonitorInfo>() };
        if (monitor == 0 || !NativeMethods.GetMonitorInfo(monitor, ref info)) throw new Win32Exception(Marshal.GetLastWin32Error());
        return new PixelBounds(info.Work.Left, info.Work.Top, info.Work.Right - info.Work.Left, info.Work.Bottom - info.Work.Top);
    }

    private void ApplyBounds()
    {
        if (!NativeMethods.IsWindow(_parent) || !OwnsSurface()) throw new Win32Exception("바탕화면 창이 더 이상 유효하지 않습니다.");
        var origin = ParentOrigin();
        int height = _view.IsCollapsed ? (int)(NoteTheme.CollapsedHeight * _scale) : _bounds.Height;
        if (!NativeMethods.SetWindowPos(Handle, 0, _bounds.X - origin.X, _bounds.Y - origin.Y, _bounds.Width, height, NativeMethods.SwpNoActivate | NativeMethods.SwpNoZOrder))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        // A child HWND has no DWM frame; clip it to the same modest paper radius.
        var shape = (_bounds.Width, height, (int)(10 * _scale));
        if (_regionShape != shape)
        {
            nint region = NativeMethods.CreateRoundRectRgn(0, 0, _bounds.Width + 1, height + 1, shape.Item3, shape.Item3);
            if (region != 0)
            {
                if (NativeMethods.SetWindowRgn(Handle, region, true) == 0) NativeMethods.DeleteObject(region);
                else _regionShape = shape;
            }
        }
        PublishBounds();
    }

    private void PublishBounds()
    {
        var savedBounds = CaptureBounds();
        if (_publishedBounds != savedBounds)
        {
            _publishedBounds = savedBounds;
            BoundsChanged?.Invoke(this, EventArgs.Empty);
        }
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
        _regionShape = null;
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
        _view.AppearanceChanged -= OnAppearance;
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
