// ticktick-window.cs
//
// Brings the TickTick PWA window to the foreground (restoring it first if it is minimized).
// Prints nothing. Exit code 0 = window activated, 1 = no TickTick window found.
//
// Written for the C# 5 compiler that ships with Windows (no C# 6 syntax).
//
// Build once, no installs needed. /target:winexe means no console window flashes
// when YASB launches it:
//   C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe /nologo /optimize /target:winexe /out:ticktick-window.exe ticktick-window.cs

using System;
using System.Runtime.InteropServices;
using System.Text;

static class TickTickWindow
{
    delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    static extern bool EnumWindows(EnumProc callback, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int maxCount);

    [DllImport("user32.dll")]
    static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    static extern bool ShowWindow(IntPtr hWnd, int command);

    [DllImport("user32.dll")]
    static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("user32.dll")]
    static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll")]
    static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool attach);

    [DllImport("kernel32.dll")]
    static extern uint GetCurrentThreadId();

    const int SW_RESTORE = 9;

    // The PWA's window titles all start with this:
    //   "TickTick - 10:50 - Focusing", "TickTick - 11:36 - Paused", "TickTick - Focus - TickTick", ...
    const string TitlePrefix = "TickTick";

    // First visible top-level window whose title starts with the prefix.
    // EnumWindows walks windows top to bottom in z-order, so this is the topmost match.
    static IntPtr FindWindow()
    {
        IntPtr found = IntPtr.Zero;
        var buffer = new StringBuilder(256);

        EnumWindows(delegate(IntPtr hWnd, IntPtr lParam)
        {
            if (!IsWindowVisible(hWnd)) return true;
            if (GetWindowText(hWnd, buffer, buffer.Capacity) == 0) return true;

            if (buffer.ToString().StartsWith(TitlePrefix, StringComparison.Ordinal))
            {
                found = hWnd;
                return false;   // stop searching
            }
            return true;
        }, IntPtr.Zero);

        return found;
    }

    // Windows normally refuses SetForegroundWindow from a background process. Briefly
    // attaching our input queue to the current foreground window's thread lifts that restriction.
    static void Activate(IntPtr hWnd)
    {
        if (IsIconic(hWnd)) ShowWindow(hWnd, SW_RESTORE);

        IntPtr foreground = GetForegroundWindow();
        uint ignored;
        uint foregroundThread = foreground != IntPtr.Zero ? GetWindowThreadProcessId(foreground, out ignored) : 0;
        uint thisThread = GetCurrentThreadId();

        bool attached = foregroundThread != 0 && foregroundThread != thisThread &&
                        AttachThreadInput(thisThread, foregroundThread, true);

        BringWindowToTop(hWnd);
        SetForegroundWindow(hWnd);

        if (attached) AttachThreadInput(thisThread, foregroundThread, false);
    }

    static int Main()
    {
        IntPtr hWnd = FindWindow();
        if (hWnd == IntPtr.Zero) return 1;

        Activate(hWnd);
        return 0;
    }
}
