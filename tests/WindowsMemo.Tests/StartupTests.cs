using System.IO;
using Microsoft.Win32;
using WindowsMemo.Infrastructure;
using WindowsMemo.Services;

namespace WindowsMemo.Tests;

internal static class StartupTests
{
    private sealed class Store : IStartupStore
    {
        public string? Command;
        public bool FailWrite, FailDelete;
        public int Writes;
        public string? Read() => Command;
        public void Write(string command)
        { if (FailWrite) throw new UnauthorizedAccessException(); Command = command; Writes++; }
        public void Delete() { if (FailDelete) throw new UnauthorizedAccessException(); Command = null; }
    }
    internal static void Run(Action<string, Action> check)
    {
        const string exe = @"C:\Program Files\Windows Memo\WindowsMemo.exe";
        check("자동 실행 기본 꺼짐: 조회만으로 등록하지 않음", () =>
        {
            var store = new Store(); var service = new StartupService(store, exe, _ => true);
            Assert(service.GetState() == StartupState.Disabled && store.Writes == 0);
            Assert(service.GetState() == StartupState.Disabled && store.Command is null);
        });
        check("자동 실행 등록·재조회·해제와 공백 경로", () =>
        {
            var store = new Store(); var service = new StartupService(store, exe, _ => true);
            service.SetEnabled(true);
            Assert(store.Command == "\"" + exe + "\" --startup" && service.GetState() == StartupState.Enabled);
            Assert(new StartupService(store, exe, _ => true).GetState() == StartupState.Enabled);
            service.SetEnabled(false);
            Assert(store.Command is null && service.GetState() == StartupState.Disabled);
        });
        check("자동 실행 쓰기·해제 실패: 기존 등록 상태 유지", () =>
        {
            var store = new Store { FailWrite = true }; var service = new StartupService(store, exe, _ => true);
            Throws(() => service.SetEnabled(true)); Assert(service.GetState() == StartupState.Disabled);
            store.FailWrite = false; service.SetEnabled(true); string? before = store.Command;
            store.FailDelete = true; Throws(() => service.SetEnabled(false));
            Assert(store.Command == before && service.GetState() == StartupState.Enabled);
        });
        check("자동 실행 실행 파일 없음·잘못된 경로·초과 길이 거부", () =>
        {
            var store = new Store(); Throws(() => new StartupService(store, exe, _ => false).SetEnabled(true));
            foreach (string invalid in new[] { "WindowsMemo.exe", @"C:WindowsMemo.exe", @"C:\Apps\dotnet.exe", Path.Combine(@"C:\Apps", "bad" + (char)34, "WindowsMemo.exe"), Path.Combine(@"C:\Apps", "bad" + (char)10, "WindowsMemo.exe"), Path.Combine(@"C:\", new string('a', 260), "WindowsMemo.exe") })
                Throws(() => new StartupService(store, invalid, _ => true).SetEnabled(true));
            Assert(store.Command is null && store.Writes == 0);
        });
        check("자동 실행 경로 이동: 이전 등록을 조용히 변경하지 않음", () =>
        {
            var store = new Store { Command = StartupService.BuildCommand(@"C:\Old\WindowsMemo.exe") };
            var service = new StartupService(store, exe, _ => true);
            Assert(service.GetState() == StartupState.OtherLocation && store.Writes == 0);
            service.SetEnabled(false); Assert(store.Command is null);
            service.SetEnabled(true); Assert(service.GetState() == StartupState.Enabled);
        });
        check("자동 실행 레지스트리 어댑터: 격리 키 왕복·다른 값 보존", () =>
        {
            // Never use the real Run key: this test key cannot start an app at logon.
            string testPath = @"Software\WindowsMemo\Tests\" + Guid.NewGuid().ToString("N");
            var store = new RegistryStartupStore(testPath);
            try
            {
                Assert(store.Read() is null);
                using (var missing = Registry.CurrentUser.OpenSubKey(testPath)) Assert(missing is null);
                using (var key = Registry.CurrentUser.CreateSubKey(testPath)) key.SetValue("OtherApp", "keep");
                var service = new StartupService(store, exe, _ => true); service.SetEnabled(true);
                using (var key = Registry.CurrentUser.OpenSubKey(testPath))
                    Assert(key!.GetValueKind(RegistryStartupStore.ValueName) == RegistryValueKind.String);
                Assert(service.GetState() == StartupState.Enabled);
                service.SetEnabled(false);
                using (var key = Registry.CurrentUser.OpenSubKey(testPath)) Assert((string?)key!.GetValue("OtherApp") == "keep");
                using (var key = Registry.CurrentUser.OpenSubKey(testPath, writable: true))
                    key!.SetValue(RegistryStartupStore.ValueName, 123, RegistryValueKind.DWord);
                Throws(() => service.GetState());
                service.SetEnabled(false); Assert(store.Read() is null);
            }
            finally { Registry.CurrentUser.DeleteSubKey(testPath, throwOnMissingSubKey: false); }
        });
    }
    private static void Assert(bool value) { if (!value) throw new InvalidOperationException("Startup assertion failed."); }
    private static void Throws(Action action)
    {
        try { action(); } catch (Exception ex) when (StartupService.IsStartupError(ex)) { return; }
        throw new InvalidOperationException("Expected a startup registration error.");
    }
}
