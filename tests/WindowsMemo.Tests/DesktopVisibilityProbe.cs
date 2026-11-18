using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using WindowsMemo.Interop;

namespace WindowsMemo.Tests;

internal static class DesktopVisibilityProbe
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(nint window, StringBuilder name, int length);

    internal static void Run()
    {
        nint desktop = DesktopNoteHost.FindDesktopView();
        Console.WriteLine($"chosen-desktop={desktop:X}");
        if (desktop != 0) Dump(NativeMethods.GetParent(desktop), 0);
        NativeMethods.EnumWindows((window, _) =>
        {
            NativeMethods.GetWindowThreadProcessId(window, out uint pid);
            string process;
            try { process = Process.GetProcessById((int)pid).ProcessName; }
            catch (ArgumentException) { return true; }
            if (process == "WindowsMemo") Dump(window, 0);
            return true;
        }, 0);
    }

    private static void Dump(nint window, int depth)
    {
        var name = new StringBuilder(256);
        GetClassName(window, name, name.Capacity);
        NativeMethods.GetWindowThreadProcessId(window, out uint pid);
        NativeMethods.GetWindowRect(window, out var r);
        Console.WriteLine($"{new string(' ', depth * 2)}{window:X} {name} pid={pid} visible={NativeMethods.IsWindowVisible(window)} rect={r.Left},{r.Top},{r.Right},{r.Bottom} style={NativeMethods.GetWindowLongPtr(window, -16):X} ex={NativeMethods.GetWindowLongPtr(window, -20):X}");
        if (depth >= 4) return;
        nint child = 0;
        int count = 0;
        while ((child = NativeMethods.FindWindowEx(window, child, null, null)) != 0 && count++ < 60)
            Dump(child, depth + 1);
    }
}
