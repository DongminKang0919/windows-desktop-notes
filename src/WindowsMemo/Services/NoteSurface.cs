using System.ComponentModel;
using System.Windows;
using WindowsMemo.Interop;
using WindowsMemo.Models;
using WindowsMemo.Views;

namespace WindowsMemo.Services;

internal interface INoteSurface : IDisposable
{
    NoteBounds CaptureBounds();
    event EventHandler? BoundsChanged;
}

internal sealed class DesktopNoteSurface : INoteSurface
{
    private readonly DesktopNoteHost _host;
    public DesktopNoteSurface(NoteView view, NoteBounds? bounds)
    {
        _host = new DesktopNoteHost(view);
        try { _host.Open(restoredBounds: bounds); }
        catch { _host.Dispose(); throw; }
    }
    public event EventHandler? BoundsChanged { add => _host.BoundsChanged += value; remove => _host.BoundsChanged -= value; }
    public NoteBounds CaptureBounds() => _host.CaptureBounds();
    public void Dispose() => _host.Dispose();
}

internal sealed class WindowNoteSurface : INoteSurface
{
    private readonly Window _window;
    private readonly NoteView _view;
    private bool _disposed;
    public event EventHandler? BoundsChanged;
    public WindowNoteSurface(NoteView view, NoteBounds? bounds)
    {
        _view = view;
        var area = SystemParameters.WorkArea;
        double width = Math.Min(bounds?.Width ?? 420, area.Width);
        double height = Math.Min(bounds?.Height ?? 480, area.Height);
        _window = new Window
        {
            Title = "Windows 메모 · 일반 창 진단", Width = width, Height = height,
            MinWidth = Math.Min(320, width), MinHeight = Math.Min(260, height), Content = view,
            Left = Math.Clamp(bounds?.X ?? area.Left + 48, area.Left, Math.Max(area.Left, area.Right - width)),
            Top = Math.Clamp(bounds?.Y ?? area.Top + 64, area.Top, Math.Max(area.Top, area.Bottom - height)),
            WindowStartupLocation = WindowStartupLocation.Manual
        };
        _window.Closing += OnClosing;
        _window.LocationChanged += OnBounds;
        _window.SizeChanged += OnBounds;
        view.MoveRequested += OnMove;
        view.ResizeRequested += OnResize;
        view.SetMode("일반 창 진단 · 별도 메모 저장");
        try { _window.Show(); }
        catch { Dispose(); throw; }
    }
    public NoteBounds CaptureBounds() => new(_window.Left, _window.Top, _window.Width, _window.Height);
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_disposed) return;
        e.Cancel = true;
        _view.RequestClose();
    }
    private void OnBounds(object? sender, EventArgs e) => BoundsChanged?.Invoke(this, EventArgs.Empty);
    private void OnMove(object? sender, Vector delta) { _window.Left += delta.X; _window.Top += delta.Y; }
    private void OnResize(object? sender, Vector delta)
    {
        _window.Width = Math.Max(_window.MinWidth, _window.Width + delta.X);
        _window.Height = Math.Max(_window.MinHeight, _window.Height + delta.Y);
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _window.Closing -= OnClosing;
        _window.LocationChanged -= OnBounds;
        _window.SizeChanged -= OnBounds;
        _view.MoveRequested -= OnMove;
        _view.ResizeRequested -= OnResize;
        _window.Content = null;
        _window.Close();
        BoundsChanged = null;
    }
}
