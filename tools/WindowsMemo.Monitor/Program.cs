using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace WindowsMemo.Monitor;

internal sealed record Sample(DateTimeOffset Utc, double ElapsedSeconds, int Pid, DateTimeOffset ProcessStartUtc,
    string Phase, long WorkingSetBytes, long PrivateBytes, int Handles, int Threads, uint? Gdi, uint? User,
    double? CpuPercent, double? CpuIntervalSeconds, bool? UiResponsive, double? SamplingGapSeconds = null);

internal sealed record Settings(string Exe, int? Pid, double Interval, double Duration, string Output, bool Launch)
{
    internal static Settings Parse(string[] args)
    {
        string? exe = null;
        int? pid = null;
        double interval = 10, duration = 24 * 3600;
        string output = Path.Combine(AppContext.BaseDirectory, "..", "logs");
        bool launch = false;
        for (int i = 0; i < args.Length; i++)
        {
            string Value() => ++i < args.Length ? args[i] : throw new ArgumentException("Missing argument value.");
            switch (args[i])
            {
                case "--exe": exe = Path.GetFullPath(Value()); break;
                case "--pid": pid = int.Parse(Value(), CultureInfo.InvariantCulture); break;
                case "--interval-seconds": interval = double.Parse(Value(), CultureInfo.InvariantCulture); break;
                case "--duration-hours": duration = double.Parse(Value(), CultureInfo.InvariantCulture) * 3600; break;
                case "--duration-seconds": duration = double.Parse(Value(), CultureInfo.InvariantCulture); break;
                case "--output": output = Path.GetFullPath(Value()); break;
                case "--launch": launch = true; break;
                default: throw new ArgumentException("Unknown argument: " + args[i]);
            }
        }
        if (exe is null || !File.Exists(exe) || !exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Specify an existing application with --exe.");
        if (!double.IsFinite(interval) || interval < 1 || interval > 3600
            || !double.IsFinite(duration) || duration < 1 || duration > 168 * 3600 || pid <= 0 || (pid is not null && launch))
            throw new ArgumentException("Interval: 1..3600 seconds; duration: 1 second..168 hours; --pid cannot be combined with --launch.");
        return new(exe, pid, interval, duration, Path.GetFullPath(output), launch);
    }
}

internal static class Program
{
    internal static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private const int MaxSamples = 100_000;

    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        try
        {
            if (args.Length == 1 && args[0] == "--self-test") { MonitorChecks.Run(); return 0; }
            if (args.Length == 2 && args[0] == "--report")
            { Report.Write(Path.GetFullPath(args[1])); Console.WriteLine("보고서를 다시 생성했습니다."); return 0; }
            if (args.Length == 0 || args.Contains("--help"))
            {
                Console.WriteLine("WindowsMemo.Monitor --exe <WindowsMemo.exe> [--launch] [--duration-hours 8|24] [--interval-seconds 10] [--output <folder>]\n"
                    + "고정 프로세스: --pid <id> (재시작 추적 없음)\n보고서 복구: --report <session-folder>\n"
                    + "기록 중 I: 유휴 / A: 사용 중 / M: 문제 관찰 표시 / Q 또는 Ctrl+C: 감시 종료 (메모 앱은 유지)");
                return 0;
            }
            return Run(Settings.Parse(args));
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or Win32Exception or InvalidOperationException or FormatException or OverflowException or JsonException)
        { Console.Error.WriteLine($"감시 도구 오류: {ex.GetType().Name} (0x{ex.HResult:X8})\n{ex.Message}"); return 1; }
    }

    private static int Run(Settings settings)
    {
        string folder = Path.Combine(settings.Output, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "session.json"), JsonSerializer.Serialize(new
        {
            schemaVersion = 1, startedUtc = DateTimeOffset.UtcNow, startedLocal = DateTimeOffset.Now,
            monitorVersion = typeof(Program).Assembly.GetName().Version?.ToString(),
            executable = settings.Exe, fixedPid = settings.Pid, intervalSeconds = settings.Interval,
            durationSeconds = settings.Duration, logicalProcessors = Environment.ProcessorCount,
            os = RuntimeInformation.OSDescription, architecture = RuntimeInformation.OSArchitecture.ToString(),
            privacy = "No note data, window titles, screenshots, dumps, clipboard, network upload or machine/user names collected.",
            cpu = "Delta process CPU seconds / monotonic wall seconds / logicalProcessors * 100. Sleep gaps are excluded.",
            ui = "WM_NULL timeout hint on a WPF window; not proof that input, rendering or saving works."
        }, new JsonSerializerOptions(Json) { WriteIndented = true }), new UTF8Encoding(false));

        using var events = Writer(folder, "events.jsonl");
        using var samples = Writer(folder, "samples.jsonl");
        using var csv = Writer(folder, "samples.csv");
        csv.WriteLine("utc,elapsedSeconds,pid,processStartUtc,phase,workingSetBytes,privateBytes,handles,threads,gdi,user,cpuPercent,cpuIntervalSeconds,uiResponsive,samplingGapSeconds");
        void Event(string kind, object? detail = null) => events.WriteLine(JsonSerializer.Serialize(new { utc = DateTimeOffset.UtcNow, kind, detail }, Json));
        using var stop = new CancellationTokenSource();
        ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; stop.Cancel(); };
        Console.CancelKeyPress += cancel;
        TrackedProcess? tracked = null;
        var watch = Stopwatch.StartNew();
        string phase = "unspecified", reason = "duration_completed", discoveryState = "";
        int sampleCount = 0;
        double nextReport = 300, nextConsole = 0;
        Event("monitor_started");
        Console.WriteLine($"기록 폴더:\n{folder}\n간격 {settings.Interval}초 / 예정 {settings.Duration / 3600:F2}시간\nI: 유휴, A: 사용 중, M: 문제 표시, Q: 종료\n메모 본문은 수집하지 않습니다. 감시를 끝내도 메모 앱은 종료하지 않습니다.");
        try
        {
            Report.Write(folder);
            if (settings.Launch)
            {
                using var existing = Find(settings, out string launchState, out _);
                if (existing is null)
                {
                    if (launchState != "waiting_for_application")
                    {
                        Event("launch_blocked", new { state = launchState });
                        throw new InvalidOperationException("기존 앱의 경로나 권한을 확인하세요. 다른 경로의 메모 앱은 트레이에서 저장 후 종료하고 다시 시작하세요. 상태: " + launchState);
                    }
                    using var launched = Process.Start(new ProcessStartInfo(settings.Exe) { WorkingDirectory = Path.GetDirectoryName(settings.Exe)!, UseShellExecute = true });
                    Event("launch_requested");
                }
            }
            while (!stop.IsCancellationRequested && watch.Elapsed.TotalSeconds < settings.Duration)
            {
                if (!Console.IsInputRedirected)
                {
                    while (Console.KeyAvailable)
                    {
                        switch (Console.ReadKey(true).Key)
                        {
                            case ConsoleKey.Q: reason = "user_stop"; stop.Cancel(); break;
                            case ConsoleKey.I: phase = "idle"; tracked?.ResetCpu(); Event("phase", phase); Console.WriteLine("유휴 구간으로 표시했습니다."); break;
                            case ConsoleKey.A: phase = "active"; tracked?.ResetCpu(); Event("phase", phase); Console.WriteLine("사용 구간으로 표시했습니다."); break;
                            case ConsoleKey.M: Event("user_observation"); Console.WriteLine("문제 관찰 시각을 기록했습니다."); break;
                        }
                    }
                }
                if (stop.IsCancellationRequested) break;
                double now = watch.Elapsed.TotalSeconds;
                if (tracked is not null && tracked.HasExited)
                {
                    Event("process_exited", new { tracked.Pid, tracked.StartUtc, exitCode = tracked.ExitCode });
                    tracked.Dispose(); tracked = null;
                    if (settings.Pid is not null) { reason = "fixed_process_exited"; break; }
                }
                if (tracked is null)
                {
                    var process = Find(settings, out string state, out int matches);
                    if (state != discoveryState) { Event("discovery", new { state, matches }); discoveryState = state; }
                    if (process is not null)
                    {
                        tracked = new TrackedProcess(process);
                        Event("process_attached", new { tracked.Pid, tracked.StartUtc,
                            version = FileVersionInfo.GetVersionInfo(settings.Exe).ProductVersion,
                            executableSha256 = Hash(settings.Exe), assemblySha256 = Hash(Path.ChangeExtension(settings.Exe, ".dll")) });
                    }
                }
                if (tracked is not null)
                {
                    try
                    {
                        Sample sample = tracked.Read(now, phase, settings.Interval);
                        if (tracked.LastGap is { } gap) Event("sampling_gap", new { seconds = gap, cause = "sleep_or_sampling_delay", cpuExcluded = true });
                        samples.WriteLine(JsonSerializer.Serialize(sample, Json));
                        csv.WriteLine(ToCsv(sample));
                        sampleCount++;
                        if (now >= nextConsole)
                        {
                            Console.WriteLine($"{DateTime.Now:HH:mm:ss} PID {sample.Pid} | Private {sample.PrivateBytes / 1048576d:F1} MiB | WS {sample.WorkingSetBytes / 1048576d:F1} MiB | CPU {sample.CpuPercent?.ToString("F2", CultureInfo.InvariantCulture) ?? "n/a"}% | handles {sample.Handles} | {phase}");
                            nextConsole = now + 60;
                        }
                        if (sampleCount >= MaxSamples) { reason = "sample_limit"; break; }
                    }
                    catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or UnauthorizedAccessException)
                    { Event("sample_unavailable", new { tracked.Pid, error = ex.GetType().Name, hResult = ex.HResult }); }
                }
                if (now >= nextReport) { Report.Write(folder); nextReport = now + 300; }
                double delay = Math.Min(settings.Interval, settings.Duration - watch.Elapsed.TotalSeconds);
                if (delay > 0) stop.Token.WaitHandle.WaitOne(TimeSpan.FromSeconds(delay));
            }
            if (stop.IsCancellationRequested && reason == "duration_completed") reason = "user_stop";
            return sampleCount > 0 ? 0 : 2;
        }
        catch (Exception ex)
        {
            reason = "monitor_error";
            Event("monitor_error", new { error = ex.GetType().Name, hResult = ex.HResult });
            throw;
        }
        finally
        {
            Console.CancelKeyPress -= cancel;
            tracked?.Dispose();
            Event("monitor_stopped", new { reason, sampleCount, elapsedSeconds = watch.Elapsed.TotalSeconds });
            Report.Write(folder);
            Console.WriteLine($"감시 종료: {reason}\n{Path.Combine(folder, "report.html")}\n분석 시 이 세션 폴더 전체를 전달하세요. 메모 앱은 계속 실행됩니다.");
        }
    }

    private static StreamWriter Writer(string folder, string name) => new(new FileStream(Path.Combine(folder, name), FileMode.CreateNew, FileAccess.Write, FileShare.Read), new UTF8Encoding(false)) { AutoFlush = true };
    private static string? Hash(string path) => File.Exists(path) ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))) : null;
    internal static string ToCsv(Sample s) => string.Join(",", new object?[]
    { s.Utc.ToString("O"), s.ElapsedSeconds.ToString("F3", CultureInfo.InvariantCulture), s.Pid, s.ProcessStartUtc.ToString("O"), s.Phase,
      s.WorkingSetBytes, s.PrivateBytes, s.Handles, s.Threads, s.Gdi, s.User,
      s.CpuPercent?.ToString("F4", CultureInfo.InvariantCulture), s.CpuIntervalSeconds?.ToString("F3", CultureInfo.InvariantCulture), s.UiResponsive,
      s.SamplingGapSeconds?.ToString("F3", CultureInfo.InvariantCulture) });

    private static Process? Find(Settings settings, out string state, out int matches)
    {
        Process? result = null;
        matches = 0;
        bool denied = false, otherPath = false;
        foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(settings.Exe)))
        {
            bool keep = false;
            try
            {
                if ((settings.Pid is null || process.Id == settings.Pid) && !process.HasExited)
                {
                    if (string.Equals(process.MainModule?.FileName, settings.Exe, StringComparison.OrdinalIgnoreCase))
                    { matches++; if (matches == 1) { result = process; keep = true; } }
                    else otherPath = true;
                }
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or UnauthorizedAccessException) { denied = true; }
            finally { if (!keep) process.Dispose(); }
        }
        state = matches > 1 ? "multiple_matching_processes" : matches == 1 ? "attached" : denied ? "access_denied_or_race" : otherPath ? "other_path_running" : "waiting_for_application";
        if (matches > 1) { result?.Dispose(); return null; }
        return result;
    }
}

internal sealed class TrackedProcess(Process process) : IDisposable
{
    internal int Pid { get; } = process.Id;
    internal DateTimeOffset StartUtc { get; } = process.StartTime.ToUniversalTime();
    internal double? LastGap { get; private set; }
    private double? _previousTime, _previousCpu;
    internal void ResetCpu() { _previousTime = null; _previousCpu = null; }
    internal bool HasExited => process.HasExited;
    internal int? ExitCode { get { try { return process.ExitCode; } catch (InvalidOperationException) { return null; } } }

    internal Sample Read(double elapsed, string phase, double interval)
    {
        process.Refresh();
        double cpu = process.TotalProcessorTime.TotalSeconds;
        double? span = _previousTime is { } previous ? elapsed - previous : null;
        LastGap = span > Math.Max(interval * 3, interval + 5) ? span : null;
        double? percent = CpuPercent(cpu, _previousCpu, span, Environment.ProcessorCount, LastGap is not null);
        _previousTime = elapsed; _previousCpu = cpu;
        return new(DateTimeOffset.UtcNow, elapsed, Pid, StartUtc, phase, process.WorkingSet64, process.PrivateMemorySize64,
            process.HandleCount, process.Threads.Count, Native.GuiCount(process.Handle, 0), Native.GuiCount(process.Handle, 1),
            percent, percent is null ? null : span, Native.Responsive(Pid), LastGap);
    }

    internal static double? CpuPercent(double cpu, double? previousCpu, double? span, int processors, bool gap)
        => !gap && span > 0 && processors > 0 && previousCpu is { } previous && cpu >= previous
            ? Math.Clamp((cpu - previous) / span.Value / processors * 100, 0, 100) : null;

    public void Dispose() => process.Dispose();
}

internal static class Native
{
    private delegate bool WindowCallback(nint window, nint data);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint GetGuiResources(nint process, uint flag);
    [DllImport("user32.dll")] private static extern bool EnumWindows(WindowCallback callback, nint data);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(nint window, StringBuilder name, int length);
    [DllImport("user32.dll", SetLastError = true)] private static extern nint SendMessageTimeout(nint window, uint message, nint wParam, nint lParam, uint flags, uint timeout, out nuint result);
    internal static uint? GuiCount(nint process, uint type)
    {
        Marshal.SetLastPInvokeError(0);
        uint count = GetGuiResources(process, type);
        return count == 0 && Marshal.GetLastPInvokeError() != 0 ? null : count;
    }
    internal static bool? Responsive(int pid)
    {
        nint target = 0;
        EnumWindows((window, _) =>
        {
            GetWindowThreadProcessId(window, out uint owner);
            if (owner != pid) return true;
            var name = new StringBuilder(128);
            GetClassName(window, name, name.Capacity);
            if (!name.ToString().StartsWith("HwndWrapper[", StringComparison.Ordinal)) return true;
            target = window; return false;
        }, 0);
        if (target == 0) return null;
        Marshal.SetLastPInvokeError(0);
        bool responded = SendMessageTimeout(target, 0, 0, 0, 3, 250, out _) != 0;
        return responded ? true : Marshal.GetLastPInvokeError() is 0 or 1460 ? false : null;
    }
}
