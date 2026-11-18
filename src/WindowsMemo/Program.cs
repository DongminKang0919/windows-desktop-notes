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
        // Installer hooks must finish before WPF, data loading, or the instance mutex.
        Velopack.VelopackApp.Build().SetAutoApplyOnStartup(false).Run();

        bool startup = args.Contains("--startup");
        bool updateTest = args.Length == 2 && args[0] == "--update-test-source";
        if (!updateTest && (args.Any(a => a is not ("--window" or "--startup")) || (startup && args.Contains("--window"))))
        {
            MessageBox.Show("일반 창 진단 모드는 --window로 실행하세요.", "Windows 메모");
            return 2;
        }
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        using var activation = new InstanceActivation();
        using var instance = new Mutex(true, "Local\\WindowsMemo.Prototype", out bool isFirst);
        if (!isFirst)
        {
            if (!startup) activation.Signal();
            return 0;
        }
        try
        {
            bool diagnostic = args.Contains("--window");
            string root = diagnostic ? Path.Combine(FileNoteRepository.DefaultRoot, "Diagnostic") : FileNoteRepository.DefaultRoot;
            using var workspace = new NoteWorkspace(root, (view, bounds) => diagnostic
                ? new WindowNoteSurface(view, bounds) : new DesktopNoteSurface(view, bounds));
            using var updater = updateTest ? new UpdaterService(new VelopackUpdateBackend(args[1])) : null;
            using var tray = new TrayController(app, workspace, diagnostic ? null : StartupService.ForCurrentBuild(), updater);
            bool initialized = false, activationPending = false;
            activation.Listen(() => app.Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() =>
            {
                if (initialized) tray.ShowList();
                else activationPending = true;
            })));
            using var blockReason = new Interop.SessionEndBlockReason();
            using var sessionEnding = new SessionEndingCoordinator(() => workspace.SessionEndState,
                workspace.FlushAllAsync, action => app.Dispatcher.BeginInvoke(DispatcherPriority.Background, action),
                result => { if (result == SessionPreparationResult.Ready) blockReason.Clear(); tray.ShowSessionPreparation(result); });
            app.Startup += async (_, _) =>
            {
                try
                {
                    // Keep WPF dispatcher continuations even when the tray uses WinForms.
                    SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));
                    await workspace.InitializeAsync(startup ? async () =>
                    {
                        // Explorer may not have created the desktop view yet at logon.
                        for (int attempt = 0; attempt < 10 && WindowsMemo.Interop.DesktopNoteHost.FindDesktopView() == 0; attempt++)
                            await Task.Delay(1000);
                    } : null);
                    initialized = true;
                    // Manual-launch preference does not suppress an explicit second-launch request
                    // or errors that need a reachable normal window.
                    if ((!startup && workspace.Settings.Current.ShowListOnLaunch) || activationPending || workspace.Items.Any(item => item.Error is not null)) tray.ShowList();
                    if (workspace.Items.Any(item => item.Error is not null)) MessageBox.Show("일부 메모를 복원하지 못했습니다. 파일은 보존되며 목록에서 다시 시도할 수 있습니다.", "메모 복원 확인");
                }
                catch (Exception ex) when (NoteWorkspace.IsNoteError(ex))
                {
                    MessageBox.Show("메모를 열지 못했습니다. 저장 파일은 보존됩니다.\n" + ex.Message, "Windows 메모", MessageBoxButton.OK, MessageBoxImage.Error);
                    app.Shutdown(1);
                }
            };
            void OnSessionEnding(object? sender, SessionEndingCancelEventArgs e)
            {
                var decision = sessionEnding.Decide();
                e.Cancel = decision.Cancel;
                if (e.Cancel)
                {
                    blockReason.Register(decision.State);
                    sessionEnding.PrepareAfterCancellation();
                }
                else blockReason.Clear();
            }
            app.SessionEnding += OnSessionEnding;
            try { return app.Run(); }
            finally { app.SessionEnding -= OnSessionEnding; }
        }
        catch (Exception ex) when (NoteWorkspace.IsNoteError(ex))
        {
            MessageBox.Show("메모를 열지 못했습니다. 저장 파일은 보존됩니다.\n" + ex.Message, "Windows 메모", MessageBoxButton.OK, MessageBoxImage.Error);
            return 1;
        }
        finally { instance.ReleaseMutex(); }
    }
}
