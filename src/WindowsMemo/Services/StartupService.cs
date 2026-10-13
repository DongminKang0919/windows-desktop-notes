using System.IO;
using System.Security;
using WindowsMemo.Infrastructure;

namespace WindowsMemo.Services;

internal enum StartupState { Disabled, Enabled, OtherLocation }

internal sealed class StartupService
{
    private readonly IStartupStore _store;
    private readonly string _executable;
    private readonly Func<string, bool> _exists;
    internal StartupService(IStartupStore store, string executable, Func<string, bool>? exists = null)
    { _store = store; _executable = executable; _exists = exists ?? File.Exists; }
    public static StartupService ForCurrentBuild() => new(new RegistryStartupStore(),
        Path.Combine(AppContext.BaseDirectory, "WindowsMemo.exe"));

    internal static string BuildCommand(string executable)
    {
        if (string.IsNullOrWhiteSpace(executable) || !Path.IsPathFullyQualified(executable)
            || executable.Any(ch => char.IsControl(ch) || ch == '"')
            || !string.Equals(Path.GetFileName(executable), "WindowsMemo.exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("자동 실행에는 WindowsMemo.exe의 절대 경로가 필요합니다.");
        string command = $"\"{Path.GetFullPath(executable)}\" --startup";
        // Windows documents a 260-character limit for Run command lines.
        if (command.Length > 260) throw new InvalidDataException("실행 경로가 너무 길어 자동 실행을 등록할 수 없습니다.");
        return command;
    }
    public StartupState GetState()
    {
        string? registered = _store.Read();
        if (registered is null) return StartupState.Disabled;
        return string.Equals(registered, BuildCommand(_executable), StringComparison.OrdinalIgnoreCase)
            ? StartupState.Enabled : StartupState.OtherLocation;
    }
    public void SetEnabled(bool enabled)
    {
        if (enabled)
        {
            string command = BuildCommand(_executable);
            if (!_exists(_executable)) throw new FileNotFoundException("WindowsMemo.exe를 찾지 못했습니다. 빌드된 실행 파일 위치에서 다시 시도하세요.");
            _store.Write(command);
            if (GetState() != StartupState.Enabled) throw new IOException("자동 실행 등록을 확인하지 못했습니다.");
        }
        else
        {
            _store.Delete();
            if (_store.Read() is not null) throw new IOException("자동 실행 해제를 확인하지 못했습니다.");
        }
    }
    internal static bool IsStartupError(Exception ex) => ex is InvalidDataException or IOException or UnauthorizedAccessException
        or SecurityException or ArgumentException or InvalidOperationException;
}
