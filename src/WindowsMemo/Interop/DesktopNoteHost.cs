using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using WindowsMemo.Views;

namespace WindowsMemo.Interop;

/// <summary>Experimental child surface in Explorer's desktop view. Does not alter or destroy Explorer windows.</summary>
public sealed class DesktopNoteHost : IDisposable
{
    private readonly NoteView _view;
    private HwndSource? _source;
    private nint _parent;
    private int _x, _y, _width, _height;
    private bool _disposed;
    public nint Handle => _source?.Handle ?? 0;

    public DesktopNoteHost(NoteView view) => _view = view;

    public void Open(bool visible = true)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_source is not null) throw new InvalidOperationException("메모 창은 이미 열려 있습니다.");
        _parent = FindDesktopView();
        if (_parent == 0) throw new InvalidOperationException("Windows 바탕화면 창을 찾지 못했습니다.");
        double scale = Math.Max(96, NativeMethods.GetDpiForWindow(_parent)) / 96.0;
        _x = (int)(48 * scale); _y = (int)(64 * scale);
        _width = (int)(420 * scale); _height = (int)(480 * scale);
        ClampBounds();
        var parameters = new HwndSourceParameters("Windows 메모 · 바탕화면 시제품", _width, _height)
        {
            ParentWindow = _parent, PositionX = _x, PositionY = _y,
            WindowStyle = NativeMethods.WsChild | NativeMethods.WsClipSiblings | (visible ? NativeMethods.WsVisible : 0),
            TreatAsInputRoot = true, HwndSourceHook = WindowHook
        };
        _source = new HwndSource(parameters);
        _source.RootVisual = _view;
        _view.MoveRequested += OnMove;
        _view.ResizeRequested += OnResize;
        try { ApplyBounds(); }
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

    private nint WindowHook(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        // A child HWND needs its owning desktop activated before WPF receives keyboard input.
        if (message == 0x0021) // WM_MOUSEACTIVATE
        {
            NativeMethods.SetForegroundWindow(NativeMethods.GetAncestor(hwnd, NativeMethods.GaRoot));
            NativeMethods.SetFocus(hwnd);
            handled = true;
            return 1; // MA_ACTIVATE; do not let the parent's desktop handler discard the click.
        }
        return 0;
    }

    private void OnMove(object? sender, Vector delta)
    {
        double scale = _source?.CompositionTarget?.TransformToDevice.M11 ?? 1;
        _x += (int)Math.Round(delta.X * scale); _y += (int)Math.Round(delta.Y * scale);
        UpdateBounds();
    }
    private void OnResize(object? sender, Vector delta)
    {
        double scale = _source?.CompositionTarget?.TransformToDevice.M11 ?? 1;
        _width = Math.Max((int)(320 * scale), _width + (int)Math.Round(delta.X * scale));
        _height = Math.Max((int)(260 * scale), _height + (int)Math.Round(delta.Y * scale));
        UpdateBounds();
    }
    private void UpdateBounds()
    {
        try { ClampBounds(); ApplyBounds(); }
        catch (Win32Exception)
        {
            _view.SetMode("바탕화면 연결 오류 · 다시 실행하여 확인하세요.");
        }
    }
    private void ClampBounds()
    {
        if (!NativeMethods.GetClientRect(_parent, out var client)) throw new Win32Exception(Marshal.GetLastWin32Error());
        int width = Math.Max(1, client.Right - client.Left), height = Math.Max(1, client.Bottom - client.Top);
        _width = Math.Min(_width, width); _height = Math.Min(_height, height);
        _x = Math.Clamp(_x, 0, width - _width); _y = Math.Clamp(_y, 0, height - _height);
    }
    private void ApplyBounds()
    {
        if (!NativeMethods.IsWindow(_parent) || Handle == 0) throw new Win32Exception("바탕화면 창이 더 이상 유효하지 않습니다.");
        if (!NativeMethods.SetWindowPos(Handle, 0, _x, _y, _width, _height, NativeMethods.SwpNoActivate))
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _view.MoveRequested -= OnMove;
        _view.ResizeRequested -= OnResize;
        if (_source is not null)
        {
            _source.RemoveHook(WindowHook);
            _source.RootVisual = null;
            _source.Dispose();
            _source = null;
        }
    }
}
