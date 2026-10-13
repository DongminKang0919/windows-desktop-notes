using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Interop;
using WindowsMemo.Views;
using WindowsMemo.Interop;

namespace WindowsMemo.Tests;

internal static class Program
{
    private static int _passed;
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            Check("한글 및 여러 줄 삽입", () =>
            {
                using var view = new NoteView();
                Assert(view.InsertPlainText("한글 메모\n두 번째 줄"));
                Assert(Text(view).Contains("한글 메모"));
                Assert(Text(view).Contains("두 번째 줄"));
            });
            Check("입력 한도 초과 시 기존 내용 보존", () =>
            {
                using var view = new NoteView();
                view.InsertPlainText("기존 내용");
                string before = Text(view);
                Assert(!view.InsertPlainText(new string('가', NoteView.MaxCharacters + 1)));
                Assert(Text(view) == before);
            });
            Check("선택 영역 교체 및 색상", () =>
            {
                using var view = new NoteView();
                view.InsertPlainText("기존 내용");
                view.TextEditor.SelectAll();
                view.ApplyColor(Brushes.RoyalBlue);
                Assert(view.TextEditor.Selection.GetPropertyValue(TextElement.ForegroundProperty) is SolidColorBrush brush && brush.Color == Colors.RoyalBlue);
                Assert(view.InsertPlainText("교체"));
                Assert(Text(view).Trim() == "교체");
            });
            Check("붙여넣기: XAML 대신 텍스트만", () =>
            {
                using var view = new NoteView();
                var data = new DataObject();
                data.SetData(DataFormats.UnicodeText, "안전한 텍스트", false);
                data.SetData(DataFormats.Xaml, "<악성문서/>", false);
                var ev = new DataObjectPastingEventArgs(data, false, DataFormats.Xaml);
                view.TextEditor.RaiseEvent(ev);
                Assert(ev.CommandCancelled);
                Assert(Text(view).Trim() == "안전한 텍스트");
            });
            Check("붙여넣기: 서식 데이터만 있으면 거부", () =>
            {
                using var view = new NoteView();
                view.InsertPlainText("보존");
                var data = new DataObject();
                data.SetData(DataFormats.Rtf, "{\\rtf1 untrusted}", false);
                var ev = new DataObjectPastingEventArgs(data, false, DataFormats.Rtf);
                view.TextEditor.RaiseEvent(ev);
                Assert(ev.CommandCancelled);
                Assert(Text(view).Trim() == "보존");
                Assert(!view.TextEditor.AllowDrop);
                Assert(view.TextEditor.UndoLimit == 100);
            });
            Check("Dispose는 반복 호출 가능, 입력 재사용 거부", () =>
            {
                var view = new NoteView();
                view.Dispose(); view.Dispose();
                bool threw = false;
                try { view.InsertPlainText("test"); } catch (ObjectDisposedException) { threw = true; }
                Assert(threw);
            });
            Check("서식 명령 및 목록 적용", () =>
            {
                using var view = new NoteView();
                using var source = new HwndSource(new HwndSourceParameters("서식 검증", 420, 480)
                {
                    WindowStyle = unchecked((int)0x80000000), TreatAsInputRoot = true
                });
                source.RootVisual = view;
                view.InsertPlainText("서식 검증");
                view.TextEditor.SelectAll();
                view.ToggleBold();
                Assert(view.TextEditor.Selection.GetPropertyValue(TextElement.FontWeightProperty).Equals(FontWeights.Bold));
                view.ToggleBullets();
                Assert(view.TextEditor.Document.Blocks.FirstBlock is System.Windows.Documents.List list && list.MarkerStyle == TextMarkerStyle.Disc);
                view.ToggleNumbering();
                Assert(view.TextEditor.Document.Blocks.FirstBlock is System.Windows.Documents.List numbered && numbered.MarkerStyle == TextMarkerStyle.Decimal);
                source.RootVisual = null;
            });
            Check("폐기한 편집기 수집 가능", () =>
            {
                var references = Enumerable.Range(0, 20).Select(_ => CreateDisposedView()).ToArray();
                GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                Assert(references.All(reference => !reference.IsAlive));
            });
            Check("데스크톱 호스트 Dispose 및 재사용 거부", () =>
            {
                using var view = new NoteView();
                var host = new DesktopNoteHost(view);
                host.Dispose(); host.Dispose();
                bool threw = false;
                try { host.Open(); } catch (ObjectDisposedException) { threw = true; }
                Assert(threw);
            });
            if (args.Contains("--shell-probe"))
            {
                Check("실제 데스크톱 호스트 생성/해제", () =>
                {
                    using var view = new NoteView();
                    using var host = new DesktopNoteHost(view);
                    Assert(DesktopNoteHost.FindDesktopView() != 0);
                    host.Open(visible: false);
                    Assert(host.Handle != 0);
                    var source = HwndSource.FromHwnd(host.Handle);
                    Assert(source is not null && source.RootVisual == view);
                    host.Dispose();
                    Assert(host.Handle == 0);
                });
            }
            Console.WriteLine($"PASS: {_passed} checks. Win+D / IME / visual behavior are not covered.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FAIL: {ex}");
            return 1;
        }
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference CreateDisposedView()
    {
        var view = new NoteView();
        view.InsertPlainText("반복 시험");
        view.Dispose();
        return new WeakReference(view);
    }
    private static string Text(NoteView view) => new TextRange(view.TextEditor.Document.ContentStart, view.TextEditor.Document.ContentEnd).Text;
    private static void Assert(bool condition)
    {
        if (!condition) throw new InvalidOperationException("Assertion failed.");
    }
    private static void Check(string name, Action action)
    {
        action(); _passed++; Console.WriteLine($"PASS {name}");
    }
}
