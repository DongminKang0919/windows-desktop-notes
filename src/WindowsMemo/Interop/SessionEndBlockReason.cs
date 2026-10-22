using System.Runtime.InteropServices;
using System.Windows.Interop;
using WindowsMemo.Services;

namespace WindowsMemo.Interop;

// Owns ONLY its own hidden top-level window; never registers a reason on an Explorer HWND.
internal sealed class SessionEndBlockReason : IDisposable
{
    private HwndSource? _source;
    private bool _disposed;
    internal nint Handle => _source?.Handle ?? 0;
    internal bool IsRegistered { get; private set; }
    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShutdownBlockReasonCreate(nint window, string reason);
    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShutdownBlockReasonDestroy(nint window);

    internal bool Register(SessionEndState state)
    {
        if (_disposed) return false;
        if (state == SessionEndState.Saved) { Clear(); return false; }
        try
        {
            _source ??= new HwndSource(new HwndSourceParameters("Windows 메모 · 종료 준비")
                { WindowStyle = unchecked((int)0x80000000), Width = 0, Height = 0 });
            string reason = state switch
            {
                SessionEndState.Preparing => "메모 앱이 준비 중입니다. 잠시 후 다시 시도하세요.",
                SessionEndState.Busy => "메모 작업 중입니다. 작업 완료 후 다시 시도하세요.",
                SessionEndState.Failed => "최신 메모를 저장하지 못했습니다. 저장을 다시 시도하세요.",
                _ => "최신 메모를 저장하고 있습니다. 잠시 후 다시 시도하세요."
            };
            bool registered = ShutdownBlockReasonCreate(_source.Handle, reason);
            if (registered) IsRegistered = true;
            return registered; // A failed update must not forget an earlier successful registration.
        }
        catch (Exception ex) when (NoteWorkspace.IsNoteError(ex)) { return false; }
    }
    internal void Clear()
    {
        if (_source is not null && IsRegistered && ShutdownBlockReasonDestroy(_source.Handle)) IsRegistered = false;
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; Clear(); _source?.Dispose(); _source = null; IsRegistered = false;
    }
}
