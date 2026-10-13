using System.IO;
using System.Windows;
using System.Windows.Threading;
using WindowsMemo.Infrastructure;
using WindowsMemo.Services;

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
            MessageBox.Show("메모 앱이 이미 실행 중입니다. 트레이의 Windows 메모에서 메모 목록을 여세요.", "Windows 메모");
            return 0;
        }
        try
        {
            bool diagnostic = args.Contains("--window");
            string root = diagnostic ? Path.Combine(FileNoteRepository.DefaultRoot, "Diagnostic") : FileNoteRepository.DefaultRoot;
            using var workspace = new NoteWorkspace(root, (view, bounds) => diagnostic
                ? new WindowNoteSurface(view, bounds) : new DesktopNoteSurface(view, bounds));
            using var tray = new TrayController(app, workspace);
            app.Startup += async (_, _) =>
            {
                try
                {
                    // Keep WPF dispatcher continuations even when the tray uses WinForms.
                    SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));
                    await workspace.InitializeAsync();
                    if (workspace.Items.All(item => item.Hidden || item.Error is not null)) tray.ShowList();
                    if (workspace.Items.Any(item => item.Error is not null)) MessageBox.Show("일부 메모를 복원하지 못했습니다. 파일은 보존되며 목록에서 다시 시도할 수 있습니다.", "메모 복원 확인");
                }
                catch (Exception ex) when (NoteWorkspace.IsNoteError(ex))
                {
                    MessageBox.Show("메모를 열지 못했습니다. 저장 파일은 보존됩니다.\n" + ex.Message, "Windows 메모", MessageBoxButton.OK, MessageBoxImage.Error);
                    app.Shutdown(1);
                }
            };
            app.SessionEnding += (_, e) =>
            {
                // Refuse session shutdown when the latest data cannot be flushed in time.
                e.Cancel = true;
                _ = tray.ExitAsync();
            };
            return app.Run();
        }
        catch (Exception ex) when (NoteWorkspace.IsNoteError(ex))
        {
            MessageBox.Show("메모를 열지 못했습니다. 저장 파일은 보존됩니다.\n" + ex.Message, "Windows 메모", MessageBoxButton.OK, MessageBoxImage.Error);
            return 1;
        }
        finally { instance.ReleaseMutex(); }
    }
}
