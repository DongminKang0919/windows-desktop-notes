using System.IO;
using System.Windows;
using WindowsMemo.Infrastructure;
using WindowsMemo.Interop;
using WindowsMemo.Services;
using WindowsMemo.Views;
using WindowsMemo.Models;

namespace WindowsMemo;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Any(a => a is not "--window"))
        {
            MessageBox.Show("일반 창 진단 모드는 --window로 실행하세요.", "Windows 메모");
            return 2;
        }
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        using var instance = new Mutex(true, "Local\\WindowsMemo.Prototype", out bool isFirst);
        if (!isFirst)
        {
            MessageBox.Show("메모 앱이 이미 실행 중입니다. 기존 메모를 확인하세요.", "Windows 메모");
            return 0;
        }
        using var view = new NoteView();
        try
        {
            bool diagnostic = args.Contains("--window");
            var repository = new FileNoteRepository(diagnostic
                ? Path.Combine(FileNoteRepository.DefaultRoot, "Diagnostic") : FileNoteRepository.DefaultRoot);
            var restored = repository.Load(record => RtfDocumentCodec.Load(record.Rtf), out bool recovered);
            if (restored is not null) view.TextEditor.Document = RtfDocumentCodec.Load(restored.Rtf);
            if (recovered) MessageBox.Show("이전 정상 백업에서 메모를 복원했습니다. 최근 변경 일부가 없을 수 있습니다. 원본 파일은 보존됩니다.", "메모 복구");
            DateTimeOffset created = restored?.CreatedAtUtc ?? DateTimeOffset.UtcNow;
            if (diagnostic)
            {
                var window = new Window
                {
                    Title = "Windows 메모 · 일반 창 진단", Width = restored?.Bounds.Width ?? 420,
                    Height = restored?.Bounds.Height ?? 480, MinWidth = 320, MinHeight = 260,
                    Content = view, WindowStartupLocation = WindowStartupLocation.CenterScreen
                };
                if (restored is not null)
                {
                    window.WindowStartupLocation = WindowStartupLocation.Manual;
                    var area = SystemParameters.WorkArea;
                    window.Width = Math.Min(window.Width, area.Width);
                    window.Height = Math.Min(window.Height, area.Height);
                    window.Left = Math.Clamp(restored.Bounds.X, area.Left, Math.Max(area.Left, area.Right - window.Width));
                    window.Top = Math.Clamp(restored.Bounds.Y, area.Top, Math.Max(area.Top, area.Bottom - window.Height));
                }
                view.SetMode("일반 창 진단 · 별도 메모 저장");
                using var saves = new AutosaveCoordinator(view, repository,
                    () => new NoteBounds(window.Left, window.Top, window.ActualWidth, window.ActualHeight), created);
                bool closing = false, allowClose = false;
                view.CloseRequested += (_, _) => window.Close();
                window.Closing += async (_, e) =>
                {
                    if (allowClose) return;
                    e.Cancel = true;
                    if (closing) return;
                    closing = true; view.IsEnabled = false;
                    if (await saves.FlushAsync()) { allowClose = true; window.Close(); }
                    else ShowSaveFailure();
                    view.IsEnabled = true; closing = false;
                };
                window.Closed += (_, _) => app.Shutdown();
                window.LocationChanged += (_, _) => saves.MarkChanged();
                window.SizeChanged += (_, _) => saves.MarkChanged();
                window.Show();
                if (restored is null || recovered) saves.MarkChanged();
                app.Run();
            }
            else
            {
                using var host = new DesktopNoteHost(view);
                host.Open(restoredBounds: restored?.Bounds);
                using var saves = new AutosaveCoordinator(view, repository, host.CaptureBounds, created);
                host.BoundsChanged += (_, _) => saves.MarkChanged();
                bool closing = false;
                view.CloseRequested += async (_, _) =>
                {
                    if (closing) return;
                    closing = true; view.IsEnabled = false;
                    if (await saves.FlushAsync()) app.Shutdown();
                    else ShowSaveFailure();
                    view.IsEnabled = true; closing = false;
                };
                // Persist the fitted position after monitor layout changes as well as new/recovered notes.
                saves.MarkChanged();
                app.Run();
            }
            return 0;
        }
        catch (Exception ex) when (FileNoteRepository.IsStorageError(ex) || ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            MessageBox.Show("메모를 열지 못했습니다. 저장 파일은 보존됩니다.\n" + ex.Message,
                "Windows 메모", MessageBoxButton.OK, MessageBoxImage.Error);
            return 1;
        }
        finally { instance.ReleaseMutex(); }
    }
    private static void ShowSaveFailure() => MessageBox.Show(
        "저장하지 못해 종료를 취소했습니다. 내용을 유지하고 있으니 저장을 다시 시도하세요.",
        "저장 실패", MessageBoxButton.OK, MessageBoxImage.Warning);
}
