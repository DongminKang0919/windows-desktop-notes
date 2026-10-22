using System.Collections;
using System.IO;
using WindowsMemo.Infrastructure;

namespace WindowsMemo.Tests;

internal static class PendingCursorTests
{
    private sealed class TrackedEnumerator(string[] files, Action<int>? beforeMove = null) : IEnumerator<string>
    {
        private int _index = -1;
        internal int Moves, Disposals;
        public string Current => files[_index];
        object IEnumerator.Current => Current;
        public bool MoveNext() { Moves++; beforeMove?.Invoke(Moves); return ++_index < files.Length; }
        public void Reset() => throw new NotSupportedException();
        public void Dispose() => Interlocked.Increment(ref Disposals);
    }
    internal static void Run(Action<string, Action> check)
    {
        string root = Path.Combine(AppContext.BaseDirectory, "pending-cursor-tests", Guid.NewGuid().ToString("N"));
        string[] Files(string name, int count)
        {
            string folder = Path.Combine(root, name); Directory.CreateDirectory(folder);
            return Enumerable.Range(0, count).Select(_ =>
            {
                string file = Path.Combine(folder, Guid.NewGuid().ToString("N") + ".tmp");
                File.WriteAllText(file, "untouched"); return file;
            }).ToArray();
        }
        check("복구 목록 탐색: 0·1·64·65·128·129개 전체 도달·중복 없음·원본 불변", () =>
        {
            foreach (int count in new[] { 0, 1, 64, 65, 128, 129 })
            {
                string[] files = Files("boundary-" + count, count); string folder = Path.Combine(root, "boundary-" + count);
                using var cursor = new PendingNoteCursor(folder);
                var seen = new HashSet<Guid>(); int pages = 0; PendingNoteScan page;
                do
                {
                    page = cursor.NextPage(); pages++;
                    Require(page.Files.Count <= 64 && page.Skipped == 0);
                    foreach (var file in page.Files) Require(seen.Add(file.Token));
                    Require(pages <= 3);
                } while (page.Limited);
                Require(seen.Count == count && cursor.NextPage().Files.Count == 0);
                Require(files.All(path => File.ReadAllText(path) == "untouched"));
            }
        });
        check("복구 목록 탐색: 페이지당 64개와 다음 1개만 열거·끝/중간 해제", () =>
        {
            var files = Files("budget", 129);
            var tracked = new TrackedEnumerator(files);
            using (var cursor = new PendingNoteCursor(Path.GetDirectoryName(files[0])!, () => tracked))
            {
                Require(cursor.NextPage().Files.Count == 64 && tracked.Moves == 65);
                Require(cursor.NextPage().Files.Count == 64 && tracked.Moves == 129);
                Require(cursor.NextPage().Files.Count == 1 && tracked.Disposals == 1);
            }
            Require(tracked.Disposals == 1);
            tracked = new TrackedEnumerator(files);
            var stopped = new PendingNoteCursor(Path.GetDirectoryName(files[0])!, () => tracked);
            stopped.NextPage(); stopped.Dispose(); stopped.Dispose(); Require(tracked.Disposals == 1);
            bool rejected = false; try { stopped.NextPage(); } catch (ObjectDisposedException) { rejected = true; }
            Require(rejected);
        });
        check("복구 목록 탐색: 무효 이름 64개 다음의 정상 후보 도달", () =>
        {
            var files = Files("invalid", 1); string folder = Path.GetDirectoryName(files[0])!;
            string[] names = Enumerable.Range(0, 64).Select(i => Path.Combine(folder, "unknown-" + i + ".tmp")).Concat(files).ToArray();
            var tracked = new TrackedEnumerator(names);
            using var cursor = new PendingNoteCursor(folder, () => tracked);
            var first = cursor.NextPage(); Require(first.Files.Count == 0 && first.Skipped == 64 && first.Limited);
            var next = cursor.NextPage(); Require(next.Files.Count == 1 && !next.Limited && tracked.Disposals == 1);
        });
        check("복구 목록 탐색: 중간 열거 실패 시 핸들 해제·처음부터 재시작", () =>
        {
            var files = Files("failure", 65); string folder = Path.GetDirectoryName(files[0])!;
            var tracked = new TrackedEnumerator(files, call => { if (call == 66) throw new IOException("Injected enumeration failure."); });
            using var cursor = new PendingNoteCursor(folder, () => tracked); Require(cursor.NextPage().Limited);
            bool failed = false; try { cursor.NextPage(); } catch (IOException) { failed = true; }
            Require(failed && tracked.Disposals == 1);
            using var restarted = new PendingNoteCursor(folder); Require(restarted.NextPage().Files.Count == 64);
        });
        check("복구 목록 탐색: 읽기 중 폐기는 창을 막지 않고 지연된 핸들 해제", () =>
        {
            var files = Files("close-in-flight", 65);
            using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
            var tracked = new TrackedEnumerator(files, call => { if (call == 1) { entered.Set(); release.Wait(TimeSpan.FromSeconds(10)); } });
            using var cursor = new PendingNoteCursor(Path.GetDirectoryName(files[0])!, () => tracked);
            var reading = Task.Run(() => { try { cursor.NextPage(); } catch (ObjectDisposedException) { } });
            try
            {
                Require(entered.Wait(TimeSpan.FromSeconds(5)));
                Require(Task.Run(cursor.Dispose).Wait(TimeSpan.FromSeconds(2)));
            }
            finally { release.Set(); }
            Require(reading.Wait(TimeSpan.FromSeconds(5)));
            Require(SpinWait.SpinUntil(() => Volatile.Read(ref tracked.Disposals) == 1, TimeSpan.FromSeconds(5)));
        });
    }
    private static void Require(bool value) { if (!value) throw new InvalidOperationException("Pending cursor assertion failed."); }
}
