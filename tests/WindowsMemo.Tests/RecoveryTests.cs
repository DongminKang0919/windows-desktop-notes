using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.IO;
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Media;
using WindowsMemo.Interop;
using WindowsMemo.Views;

namespace WindowsMemo.Tests;

internal static class RecoveryTests
{
    internal static void Run(Action<string, Action> check)
    {
        check("음수 좌표 모니터에서 작업 영역 유지", () =>
        {
            var area = new PixelBounds(-1920, -200, 1920, 1040);
            var result = new PixelBounds(-2200, -500, 420, 480).FitWithin(area, 320, 260);
            Assert(result.X == -1920 && result.Y == -200);
            Assert(result.Width == 420 && result.Height == 480);
        });
        check("모니터 제거 후 화면 밖 메모 복구", () =>
        {
            var result = new PixelBounds(2500, 1700, 420, 480).FitWithin(new PixelBounds(0, 0, 1920, 1040), 320, 260);
            Assert(result.X == 1500 && result.Y == 560);
        });
        check("작은 작업 영역과 과대 크기 제한", () =>
        {
            var result = new PixelBounds(0, 0, 4000, 3000).FitWithin(new PixelBounds(20, 30, 200, 150), 320, 260);
            Assert(result == new PixelBounds(20, 30, 200, 150));
        });
        check("DPI 크기 변환은 화면 위치 유지", () =>
        {
            var bounds = new PixelBounds(-1000, 40, 420, 480);
            Assert(bounds.ScaleSize(1, 2) == new PixelBounds(-1000, 40, 840, 960));
            Assert(bounds.ScaleSize(1, 2).ScaleSize(2, 1) == bounds);
        });
        check("유효하지 않은 배율 거부", () =>
        {
            bool rejected = false;
            try { new PixelBounds(0, 0, 420, 480).ScaleSize(1, double.NaN); }
            catch (ArgumentOutOfRangeException) { rejected = true; }
            Assert(rejected);
        });
        check("부모 창 손실 후 내용·서식 유지하며 재부착", () =>
        {
            using var firstParent = ParentSurface();
            using var secondParent = ParentSurface();
            nint parent = firstParent.Handle;
            using var view = new NoteView();
            using var host = new DesktopNoteHost(view, () => parent, watchChanges: false);
            host.Open(visible: false);
            view.InsertPlainText("복구할 한글 메모");
            view.TextEditor.SelectAll();
            view.ToggleBold();
            view.ApplyColor(Brushes.RoyalBlue);
            view.ToggleBullets();
            byte[] before = Rtf(view);
            firstParent.Dispose();
            parent = 0;
            host.CheckConnection();
            Assert(host.Handle == 0 && host.HasFallback);
            Assert(Rtf(view).SequenceEqual(before));
            parent = secondParent.Handle;
            host.CheckConnection();
            Assert(host.Handle != 0 && !host.HasFallback && host.RecoveryAttempts == 0);
            Assert(Rtf(view).SequenceEqual(before));
            Assert(view.InsertPlainText("계속 편집"));
        });
        check("바탕화면 레이아웃 재확인: 동일 위치·크기에서 저장 변경 미발행", () =>
        {
            using var parent = ParentSurface(); using var view = new NoteView();
            using var host = new DesktopNoteHost(view, () => parent.Handle, watchChanges: false);
            host.Open(visible: false);
            int changed = 0; host.BoundsChanged += (_, _) => changed++;
            for (int i = 0; i < 20; i++) host.CheckConnection(refreshLayout: true);
            view.SetPaper("Sage"); view.SetCollapsed(true); view.SetCollapsed(false);
            Assert(changed == 0);
        });
        check("재시도 한도와 수동 재연결", () =>
        {
            using var firstParent = ParentSurface();
            using var replacement = ParentSurface();
            nint parent = firstParent.Handle;
            int lookups = 0;
            using var view = new NoteView();
            using var host = new DesktopNoteHost(view, () => { lookups++; return parent; }, watchChanges: false);
            host.Open(visible: false);
            view.InsertPlainText("보존할 내용");
            firstParent.Dispose();
            parent = 0;
            for (int i = 0; i < 30; i++) host.CheckConnection();
            Assert(host.RecoveryStopped && host.HasFallback);
            Assert(lookups == 1 + DesktopNoteHost.MaxRecoveryAttempts);
            parent = replacement.Handle;
            host.RetryConnection();
            Assert(host.Handle != 0 && !host.RecoveryStopped && !host.HasFallback);
            Assert(Text(view).Contains("보존할 내용"));
        });
        check("바탕화면 없는 첫 실행: 임시 편집·재시도 제한·늦은 연결", () =>
        {
            using var replacement = ParentSurface();
            nint parent = 0;
            using var view = new NoteView();
            using var host = new DesktopNoteHost(view, () => parent, watchChanges: false);
            host.Open(visible: false, restoredBounds: new WindowsMemo.Models.NoteBounds(80, 90, 320, 300, 1.5));
            Assert(host.HasFallback && host.Handle == 0 && host.RecoveryAttempts == 0);
            Assert(host.CaptureBounds() == new WindowsMemo.Models.NoteBounds(80, 90, 320, 300, 1.5));
            view.InsertPlainText("바탕화면 준비 전에도 편집 가능");
            for (int i = 0; i < 30; i++) host.CheckConnection();
            Assert(host.RecoveryStopped && host.HasFallback);
            parent = replacement.Handle;
            host.RetryConnection();
            Assert(host.Handle != 0 && !host.HasFallback && !host.RecoveryStopped);
            Assert(Text(view).Contains("바탕화면 준비 전에도 편집 가능"));
            var bounds = host.CaptureBounds();
            Assert(bounds.X == 80 && bounds.Y == 90 && bounds.Width == 320 && bounds.Height == 300);
        });
        check("20회 표면 재생성 후 원래 내용 유지", () =>
        {
            using var view = new NoteView();
            HwndSource parent = ParentSurface();
            using var host = new DesktopNoteHost(view, () => parent.Handle, watchChanges: false);
            try
            {
                host.Open(visible: false);
                view.InsertPlainText("반복 복구 메모");
                for (int i = 0; i < 20; i++)
                {
                    parent.Dispose();
                    parent = ParentSurface();
                    host.CheckConnection();
                    Assert(host.Handle != 0 && !host.HasFallback);
                }
                Assert(Text(view).Contains("반복 복구 메모"));
                host.Dispose();
                Assert(host.Handle == 0);
            }
            finally { parent.Dispose(); }
        });
        check("복구 중단 후 TaskbarCreated 수신: 제한 재시작과 본문·상태 보존", () =>
            ResumeAfterNotification(power: false));
        check("복구 중단 후 절전 복귀 수신: 대기 알림 병합과 제한 재시작", () =>
            ResumeAfterNotification(power: true));
        check("복구 호스트 및 편집기 참조 해제", () =>
        {
            var weak = DisposedHostReferences();
            System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            Assert(weak.All(reference => !reference.IsAlive));
        });
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint SendMessage(nint hwnd, uint message, nint wParam, nint lParam);

    private static void DrainMessages() => System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(
        () => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);

    private static void ResumeAfterNotification(bool power)
    {
        using var first = ParentSurface(); using var replacement = ParentSurface();
        nint parent = first.Handle;
        int lookups = 0;
        using var view = new NoteView();
        using var host = new DesktopNoteHost(view, () => { lookups++; return parent; }, watchChanges: true);
        host.Open(visible: false);
        view.InsertPlainText("늦은 복구에도 보존할 메모");
        view.TextEditor.SelectAll(); view.ToggleBold(); view.ApplyColor(Brushes.RoyalBlue);
        view.SetPaper("Peach"); view.SetCollapsed(true);
        string before = Text(view); var bounds = host.CaptureBounds();
        // Send only to this process's hidden observer; never broadcast to Explorer or other apps.
        nint observer = FindObserver();
        Assert(observer != 0);
        first.Dispose(); parent = 0;
        for (int i = 0; i < DesktopNoteHost.MaxRecoveryAttempts; i++) host.CheckConnection();
        Assert(host.RecoveryStopped && host.HasFallback);
        // Existing error UI expands a collapsed note to expose its reconnect action.
        Assert(!view.IsCollapsed && view.Paper == "Peach" && Text(view) == before);
        int stoppedLookups = lookups;
        SendMessage(observer, 0x001A, 0, 0); // Settings alone must not restart a stopped retry loop.
        SendMessage(observer, 0x0218, 0x0A, 0); // Battery/power status change is not resume.
        DrainMessages();
        Assert(host.RecoveryStopped && lookups == stoppedLookups);
        // A real recovery signal arriving behind a queued settings check must not be lost.
        SendMessage(observer, 0x001A, 0, 0);
        uint message = power ? 0x0218u : NativeMethods.RegisterWindowMessage("TaskbarCreated");
        for (int i = 0; i < 20; i++) SendMessage(observer, message, power ? 0x12 : 0, 0);
        DrainMessages();
        Assert(!host.RecoveryStopped && host.RecoveryAttempts == 1 && lookups == stoppedLookups + 1);
        SendMessage(observer, message, power ? 7 : 0, 0);
        DrainMessages();
        Assert(host.RecoveryAttempts == 2 && lookups == stoppedLookups + 2);
        // The new retry budget remains bounded even if the desktop still has not appeared.
        for (int i = 0; i < 30; i++) host.CheckConnection();
        Assert(host.RecoveryStopped && lookups == stoppedLookups + DesktopNoteHost.MaxRecoveryAttempts);
        parent = replacement.Handle;
        // A fresh user choice made in the fallback must survive successful reattachment.
        view.SetCollapsed(true);
        SendMessage(observer, message, power ? 7 : 0, 0);
        DrainMessages();
        Assert(host.Handle != 0 && !host.HasFallback && host.RecoveryAttempts == 0);
        Assert(Text(view) == before);
        Assert(view.Paper == "Peach" && view.IsCollapsed);
        var range = new TextRange(view.TextEditor.Document.ContentStart, view.TextEditor.Document.ContentEnd.GetPositionAtOffset(-1)!);
        Assert(range.GetPropertyValue(TextElement.FontWeightProperty).Equals(System.Windows.FontWeights.Bold));
        Assert(range.GetPropertyValue(TextElement.ForegroundProperty) is SolidColorBrush brush && brush.Color == Colors.RoyalBlue);
        Assert(host.CaptureBounds() == bounds);
        // Disposal cancels the effect of an already queued notification.
        SendMessage(observer, message, power ? 0x12 : 0, 0);
        int beforeDispose = lookups;
        host.Dispose(); DrainMessages();
        Assert(host.Handle == 0 && !host.HasFallback && lookups == beforeDispose);
    }

    private static nint FindObserver()
    {
        nint result = 0;
        NativeMethods.EnumWindows((hwnd, _) =>
        {
            NativeMethods.GetWindowThreadProcessId(hwnd, out uint process);
            if (process == Environment.ProcessId && WindowTitle(hwnd) == "WindowsMemo.DisplayObserver")
            { result = hwnd; return false; }
            return true;
        }, 0);
        return result;
    }
    private static string WindowTitle(nint hwnd)
    {
        var text = new System.Text.StringBuilder(128); GetWindowText(hwnd, text, text.Capacity); return text.ToString();
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(nint hwnd, System.Text.StringBuilder text, int count);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference[] DisposedHostReferences()
    {
        using var parent = ParentSurface();
        var view = new NoteView();
        var host = new DesktopNoteHost(view, () => parent.Handle, watchChanges: true);
        host.Open(visible: false);
        host.Dispose();
        view.Dispose();
        return [new WeakReference(host), new WeakReference(view)];
    }

    private static HwndSource ParentSurface() => new(new HwndSourceParameters("Recovery test parent", 1600, 900)
    {
        PositionX = 0, PositionY = 0, WindowStyle = unchecked((int)0x80000000)
    });
    private static string Text(NoteView view) => new TextRange(view.TextEditor.Document.ContentStart, view.TextEditor.Document.ContentEnd).Text;
    private static byte[] Rtf(NoteView view)
    {
        using var stream = new MemoryStream();
        new TextRange(view.TextEditor.Document.ContentStart, view.TextEditor.Document.ContentEnd).Save(stream, System.Windows.DataFormats.Rtf);
        return stream.ToArray();
    }
    private static void Assert(bool condition)
    {
        if (!condition) throw new InvalidOperationException("Recovery assertion failed.");
    }
}
