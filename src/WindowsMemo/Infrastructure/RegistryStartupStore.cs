using System.IO;
using Microsoft.Win32;

namespace WindowsMemo.Infrastructure;

internal interface IStartupStore
{
    string? Read();
    void Write(string command);
    void Delete();
}

internal sealed class RegistryStartupStore : IStartupStore
{
    internal const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    internal const string ValueName = "WindowsMemo";
    private readonly string _keyPath;
    internal RegistryStartupStore(string keyPath = RunKey) => _keyPath = keyPath;
    public string? Read()
    {
        // Reading a missing registration must not create a startup key or value.
        using var key = Registry.CurrentUser.OpenSubKey(_keyPath, writable: false);
        object? value = key?.GetValue(ValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        if (value is null) return null;
        if (key!.GetValueKind(ValueName) != RegistryValueKind.String || value is not string command)
            throw new InvalidDataException("자동 실행 등록 형식을 확인하지 못했습니다.");
        return command;
    }
    public void Write(string command)
    {
        using var key = Registry.CurrentUser.CreateSubKey(_keyPath, writable: true)
            ?? throw new IOException("자동 실행 등록 위치를 열지 못했습니다.");
        key.SetValue(ValueName, command, RegistryValueKind.String);
        key.Flush();
    }
    public void Delete()
    {
        using var key = Registry.CurrentUser.OpenSubKey(_keyPath, writable: true);
        // Remove only our named value; never delete Run or another app's entries.
        key?.DeleteValue(ValueName, throwOnMissingValue: false);
        key?.Flush();
    }
}
