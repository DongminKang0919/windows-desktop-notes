using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;

namespace WindowsMemo.Monitor;

internal sealed record ProcessSummary(int Pid, DateTimeOffset StartUtc, int Samples, double ObservedSeconds,
    bool WarmupExcluded, double PrivateBaselineMiB, double PrivateEndMiB, double PrivatePeakMiB,
    double? PrivateTrendMiBPerHour, int HandlesFirst, int HandlesLast, int HandlesPeak, uint? GdiPeak, uint? UserPeak,
    double? CpuMeanPercent, double? IdleCpuMeanPercent, double CpuCoverageSeconds, int UiTimeoutSamples);

internal static class Report
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
    internal static List<Sample> Read(string folder, out int incompleteLines)
    {
        incompleteLines = 0;
        var samples = new List<Sample>();
        string path = Path.Combine(folder, "samples.jsonl");
        if (!File.Exists(path)) return samples;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        while (reader.ReadLine() is { } line)
        {
            try
            {
                var sample = JsonSerializer.Deserialize<Sample>(line, Program.Json);
                if (sample is not null && double.IsFinite(sample.ElapsedSeconds) && sample.ElapsedSeconds >= 0
                    && sample.Pid > 0 && sample.PrivateBytes >= 0 && sample.WorkingSetBytes >= 0
                    && (sample.CpuPercent is null || double.IsFinite(sample.CpuPercent.Value))) samples.Add(sample);
                else incompleteLines++;
            }
            catch (JsonException) { incompleteLines++; }
            if (samples.Count >= 100_000) break;
        }
        return samples;
    }

    internal static ProcessSummary Summarize(IReadOnlyList<Sample> source)
    {
        var all = source.OrderBy(s => s.ElapsedSeconds).ToArray();
        var settled = all.Where(s => s.ElapsedSeconds - all[0].ElapsedSeconds >= 300).ToArray();
        bool warmed = settled.Length >= 5;
        var comparison = warmed ? settled : all;
        double Median(IEnumerable<double> data)
        {
            var sorted = data.Order().ToArray();
            return (sorted[(sorted.Length - 1) / 2] + sorted[sorted.Length / 2]) / 2;
        }
        double? slope = null;
        if (warmed && settled.Length >= 10 && settled[^1].ElapsedSeconds - settled[0].ElapsedSeconds >= 300)
        {
            double origin = settled[0].ElapsedSeconds;
            double meanX = settled.Average(s => s.ElapsedSeconds - origin);
            double meanY = settled.Average(s => s.PrivateBytes / 1048576d);
            double denominator = settled.Sum(s => Math.Pow(s.ElapsedSeconds - origin - meanX, 2));
            if (denominator > 0) slope = settled.Sum(s => (s.ElapsedSeconds - origin - meanX) * (s.PrivateBytes / 1048576d - meanY)) / denominator * 3600;
        }
        return new(all[0].Pid, all[0].ProcessStartUtc, all.Length, all[^1].ElapsedSeconds - all[0].ElapsedSeconds,
            warmed, Median(comparison.Take(5).Select(s => s.PrivateBytes / 1048576d)),
            Median(comparison.TakeLast(5).Select(s => s.PrivateBytes / 1048576d)), all.Max(s => s.PrivateBytes) / 1048576d,
            slope, all[0].Handles, all[^1].Handles, all.Max(s => s.Handles), all.Max(s => s.Gdi), all.Max(s => s.User),
            WeightedCpu(all), WeightedCpu(all.Where(s => s.Phase == "idle")),
            all.Where(s => s.CpuPercent is not null).Sum(s => s.CpuIntervalSeconds ?? 0), all.Count(s => s.UiResponsive == false));
    }

    private static double? WeightedCpu(IEnumerable<Sample> samples)
    {
        var usable = samples.Where(s => s.CpuPercent is not null && s.CpuIntervalSeconds is > 0).ToArray();
        double seconds = usable.Sum(s => s.CpuIntervalSeconds!.Value);
        return seconds > 0 ? usable.Sum(s => s.CpuPercent!.Value * s.CpuIntervalSeconds!.Value) / seconds : null;
    }

    internal static void Write(string folder)
    {
        if (!Directory.Exists(folder)) throw new DirectoryNotFoundException("Session folder does not exist.");
        var samples = Read(folder, out int incomplete);
        var groups = samples.GroupBy(s => (s.Pid, s.ProcessStartUtc)).ToArray();
        var summaries = groups.Select(g => Summarize(g.ToArray())).ToArray();
        var summary = new { generatedUtc = DateTimeOffset.UtcNow, sampleCount = samples.Count, ignoredIncompleteLines = incomplete,
            processes = summaries, verdict = "Descriptive measurements only; not a memory-leak or stability pass/fail verdict." };
        Atomic(folder, "summary.json", JsonSerializer.Serialize(summary, new JsonSerializerOptions(Program.Json) { WriteIndented = true }));

        var html = new StringBuilder("<!doctype html><html lang=\"ko\"><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><title>Windows 메모 장시간 기록</title><style>body{font:15px system-ui,sans-serif;background:#f3f5f7;color:#17212b;max-width:1120px;margin:auto;padding:28px}section{background:white;padding:20px;margin:16px 0;border-radius:12px}h1{font-size:28px}h2{font-size:19px}p{line-height:1.65}table{border-collapse:collapse;width:100%;font-size:13px}td,th{text-align:left;padding:8px;border-bottom:1px solid #ddd}svg{width:100%;height:auto}pre{white-space:pre-wrap;overflow-wrap:anywhere;font-size:12px}.muted{color:#536576}</style><h1>Windows 메모 장시간 기록</h1>");
        html.Append($"<p>생성 UTC: {DateTimeOffset.UtcNow:O} · 표본 {samples.Count:N0}개 · 프로세스 실행 구간 {groups.Length}개</p>");
        html.Append("<p>이 보고서는 측정 결과이며 누수·안정성 합격 판정이 아닙니다. Private 메모리는 프로세스 전용 할당량, Working Set은 현재 물리 메모리 사용량입니다. 초기 캐시·JIT·작업량 변화도 증가를 만들 수 있습니다. GC 힙·객체 참조는 수집하지 않습니다.</p>");
        if (incomplete > 0) html.Append($"<p>읽을 수 없는 JSONL 줄 {incomplete}개를 제외했습니다. 원본 파일은 보존했습니다.</p>");
        if (samples.Count == 0) html.Append("<section><p>아직 측정 표본이 없습니다. 앱 경로와 events.jsonl의 대기·권한 상태를 확인하세요.</p></section>");
        else
        {
            html.Append("<section><h2>실행 구간별 요약</h2><p>메모리 기준·끝 값은 각 구간의 처음·마지막 최대 5표본 중앙값입니다. 5분 워밍업 뒤 표본이 충분하면 초기 5분을 제외합니다. 추세는 그 이후 최소 10표본·5분을 확보한 구간만 계산합니다. 프로세스 재시작 전후는 합치지 않습니다.</p><div style=\"overflow:auto\"><table><tr><th>PID / 시작 UTC</th><th>관찰</th><th>Private 기준 → 끝 / 최대</th><th>추세 MiB/h</th><th>핸들 처음 → 끝 / 최대</th><th>CPU 평균 / 유휴 %</th><th>UI timeout</th></tr>");
            foreach (var s in summaries)
                html.Append($"<tr><td>{s.Pid}<br>{s.StartUtc:MM-dd HH:mm:ss}</td><td>경과 {s.ObservedSeconds / 3600:F2}h<br>CPU 표본 구간 {s.CpuCoverageSeconds / 3600:F2}h<br>{(s.WarmupExcluded ? "초기 5분 제외" : "짧은 기록")}</td><td>{F(s.PrivateBaselineMiB)} → {F(s.PrivateEndMiB)} / {F(s.PrivatePeakMiB)} MiB</td><td>{F(s.PrivateTrendMiBPerHour)}</td><td>{s.HandlesFirst} → {s.HandlesLast} / {s.HandlesPeak}</td><td>{F(s.CpuMeanPercent)} / {F(s.IdleCpuMeanPercent)}</td><td>{s.UiTimeoutSamples}</td></tr>");
            html.Append("</table></div><p class=\"muted\">CPU는 논리 CPU 전체 기준입니다. 절전·표본 지연 구간은 CPU 계산에서 제외됩니다. 유휴 평균은 콘솔에서 I를 누른 구간만 포함합니다. UI timeout은 짧은 WM_NULL 응답 검사 결과로, 렌더링·입력·저장 성공을 보장하지 않습니다.</p></section>");
            html.Append(Chart("메모리 (MiB)", samples, [("Private", "#2267b5", s => s.PrivateBytes / 1048576d), ("Working Set", "#c17624", s => s.WorkingSetBytes / 1048576d)]));
            html.Append(Chart("CPU (전체 시스템 기준 %)", samples, [("CPU", "#2267b5", s => s.CpuPercent)]));
            html.Append(Chart("프로세스 핸들·스레드", samples, [("Handles", "#2267b5", s => s.Handles), ("Threads", "#c17624", s => s.Threads)]));
            html.Append(Chart("GUI 자원", samples, [("GDI", "#2267b5", s => s.Gdi), ("USER", "#c17624", s => s.User)]));
        }
        html.Append("<section><h2>이벤트 기록 (최근 100개)</h2><p>종료·재실행·표본 공백·유휴/사용 전환·문제 관찰 시각입니다. 종료 코드만으로 정상 종료와 강제 종료를 확정하지 않습니다. 전체 기록은 events.jsonl에 있습니다.</p><pre>");
        string eventPath = Path.Combine(folder, "events.jsonl");
        if (File.Exists(eventPath))
        {
            using var eventStream = new FileStream(eventPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(eventStream, Encoding.UTF8);
            var recent = new Queue<string>();
            while (reader.ReadLine() is { } line) { recent.Enqueue(line); if (recent.Count > 100) recent.Dequeue(); }
            html.Append(WebUtility.HtmlEncode(string.Join("\n", recent)));
        }
        html.Append("</pre></section><p>메모 본문·창 제목·화면·덤프는 수집하지 않습니다. session.json에는 로컬 실행 파일 경로가 포함됩니다. 이 파일을 공유하면 경로 안의 사용자 이름도 전달될 수 있습니다.</p></html>");
        Atomic(folder, "report.html", html.ToString());
    }

    private static string Chart(string title, IReadOnlyList<Sample> samples, (string Name, string Color, Func<Sample, double?> Value)[] series)
    {
        double first = samples.Min(s => s.ElapsedSeconds), last = samples.Max(s => s.ElapsedSeconds);
        double span = Math.Max(1, last - first);
        double maximum = Math.Max(1, series.Max(line => samples.Max(s => line.Value(s) ?? 0)) * 1.08);
        var html = new StringBuilder($"<section><h2>{title}</h2><p>{string.Join(" · ", series.Select(s => $"<span style=\"color:{s.Color}\">{s.Name}</span>"))}</p><svg viewBox=\"0 0 1000 250\" role=\"img\" aria-label=\"{title}\">");
        for (int i = 0; i <= 4; i++)
        {
            double y = 210 - i * 47.5;
            html.Append($"<path d=\"M65 {N(y)} H980\" stroke=\"#e2e7ec\"/><text x=\"4\" y=\"{N(y + 4)}\" font-size=\"12\">{F(maximum * i / 4)}</text>");
        }
        foreach (var line in series)
        foreach (var process in samples.GroupBy(s => (s.Pid, s.ProcessStartUtc)))
        {
            var all = process.OrderBy(s => s.ElapsedSeconds).ToArray();
            int step = Math.Max(1, (int)Math.Ceiling(all.Length / 1500d));
            var points = all.Where((s, i) => i % step == 0 || i == all.Length - 1 || s.SamplingGapSeconds is not null
                || (i > 0 && all[i - 1].SamplingGapSeconds is not null) || line.Value(s) is null).ToArray();
            bool move = true;
            var path = new StringBuilder();
            foreach (var point in points)
            {
                double? value = line.Value(point);
                // Break across missing values, sleep gaps and restarts rather than inventing continuity.
                if (value is null) { move = true; continue; }
                if (point.SamplingGapSeconds is not null) move = true;
                path.Append($"{(move ? "M" : "L")}{N(65 + (point.ElapsedSeconds - first) / span * 915)},{N(210 - value.Value / maximum * 190)} ");
                move = false;
            }
            html.Append($"<path d=\"{path}\" fill=\"none\" stroke=\"{line.Color}\" stroke-width=\"2\"/>");
        }
        html.Append($"<text x=\"65\" y=\"240\" font-size=\"12\">{samples.Min(s => s.Utc):MM-dd HH:mm:ss} UTC</text><text x=\"980\" y=\"240\" text-anchor=\"end\" font-size=\"12\">{samples.Max(s => s.Utc):MM-dd HH:mm:ss} UTC</text></svg></section>");
        return html.ToString();
    }
    private static string N(double value) => value.ToString("F2", Invariant);
    private static string F(double? value) => value?.ToString("F2", Invariant) ?? "n/a";
    private static void Atomic(string folder, string name, string content)
    {
        string target = Path.Combine(folder, name), temporary = target + ".tmp";
        File.WriteAllText(temporary, content, new UTF8Encoding(false));
        File.Move(temporary, target, true);
    }
}
