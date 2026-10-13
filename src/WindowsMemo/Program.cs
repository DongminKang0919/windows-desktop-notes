using System.Windows;
using WindowsMemo.Interop;
using WindowsMemo.Views;

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
            MessageBox.Show("시제품이 이미 실행 중입니다. 기존 메모를 확인하세요.", "Windows 메모");
            return 0;
        }

        var view = new NoteView();
        try
        {
            if (args.Contains("--window"))
            {
                var window = new Window
                {
                    Title = "Windows 메모 · 일반 창 진단",
                    Width = 420, Height = 480, MinWidth = 320, MinHeight = 260,
                    Content = view, WindowStartupLocation = WindowStartupLocation.CenterScreen
                };
                view.SetMode("일반 창 진단 · 바탕화면 부착 아님");
                view.CloseRequested += (_, _) => window.Close();
                window.Closing += (_, e) => e.Cancel = !ConfirmExit();
                window.Closed += (_, _) => app.Shutdown();
                window.Show();
                app.Run();
            }
            else
            {
                using var host = new DesktopNoteHost(view);
                host.Open();
                view.SetMode("바탕화면 부착 실험 · Win+D로 확인");
                view.CloseRequested += (_, _) =>
                {
                    if (ConfirmExit()) app.Shutdown();
                };
                app.Run();
            }
            return 0;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            MessageBox.Show($"메모 창을 열지 못했습니다.\n{ex.Message}\n\n일반 창 진단은 --window로 실행하세요.",
                "Windows 메모", MessageBoxButton.OK, MessageBoxImage.Error);
            return 1;
        }
        finally
        {
            view.Dispose();
            instance.ReleaseMutex();
        }
    }

    private static bool ConfirmExit() => MessageBox.Show(
        "이 시제품은 메모를 저장하지 않습니다. 종료하면 입력한 내용이 사라집니다.\n종료할까요?",
        "시제품 종료", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes;
}
