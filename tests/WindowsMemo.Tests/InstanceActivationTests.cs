using WindowsMemo.Services;

namespace WindowsMemo.Tests;

internal static class InstanceActivationTests
{
    internal static void Run(Action<string, Action> check)
    {
        check("재실행: 초기화 전 요청 보존·반복 요청 전달·종료 후 수신 해제", () =>
        {
            string name = @"Local\WindowsMemo.Tests.Activate." + Guid.NewGuid().ToString("N");
            using var primary = new InstanceActivation(name);
            using var secondary = new InstanceActivation(name);
            using var received = new AutoResetEvent(false);
            int count = 0;
            secondary.Signal();
            primary.Listen(() => { Interlocked.Increment(ref count); received.Set(); });
            Assert(received.WaitOne(TimeSpan.FromSeconds(5)) && count == 1);
            secondary.Signal();
            Assert(received.WaitOne(TimeSpan.FromSeconds(5)) && count == 2);
            primary.Dispose();
            secondary.Signal();
            Assert(!received.WaitOne(TimeSpan.FromMilliseconds(100)) && count == 2);
            primary.Dispose();
        });
    }

    private static void Assert(bool condition)
    {
        if (!condition) throw new InvalidOperationException("실행 요청 전달 검증 실패");
    }
}
