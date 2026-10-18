// ticktick-focus.cs
//
// Prints the TickTick PWA's focus timer state, based on its window title:
//   "TickTick - 10:50 - Focusing"  ->  "<timer icon> 10:50"
//   "TickTick - 11:36 - Paused"    ->  "<pause icon> 11:36"
// Prints nothing when no timer is running or paused.
//
// Written for the C# 5 compiler that ships with Windows (no C# 6 syntax).
//
// Build once, no installs needed:
//   C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe /nologo /optimize /out:ticktick-focus.exe ticktick-focus.cs

using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

static class TickFocus
{
    delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    static extern bool EnumWindows(EnumProc callback, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int maxCount);

    // Window title format: "TickTick - <time> - <state>"
    const string Prefix        = "TickTick - ";
    const string FocusingSuffix = " - Focusing";
    const string PausedSuffix   = " - Paused";

    // Edit these to change the icons (U+23F3 hourglass, U+23F8 pause; U+FE0F = emoji style).
    const string FocusingIcon = "\u23F3";
    const string PausedIcon   = "\u23F8\uFE0F";

    // Returns the time part of the title if it matches "<Prefix><time><suffix>", else null.
    static string ParseTime(string title, string suffix)
    {
        if (!title.StartsWith(Prefix, StringComparison.Ordinal)) return null;
        if (!title.EndsWith(suffix, StringComparison.Ordinal)) return null;

        int length = title.Length - Prefix.Length - suffix.Length;
        if (length < 3) return null;

        string time = title.Substring(Prefix.Length, length);
        bool looksLikeTime = char.IsDigit(time[0]) && time.IndexOf(':') > 0;
        return looksLikeTime ? time : null;
    }

    static int Main()
    {
        string focusing = null;
        string paused = null;
        var buffer = new StringBuilder(256);

        EnumWindows(delegate(IntPtr hWnd, IntPtr lParam)
        {
            if (GetWindowText(hWnd, buffer, buffer.Capacity) == 0) return true;
            string title = buffer.ToString();

            string time = ParseTime(title, FocusingSuffix);
            if (time != null)
            {
                focusing = time;
                return false;   // a running timer wins; stop searching
            }

            if (paused == null) paused = ParseTime(title, PausedSuffix);
            return true;
        }, IntPtr.Zero);

        string result = null;
        if (focusing != null)      result = FocusingIcon + " " + focusing;
        else if (paused != null)   result = PausedIcon + " " + paused;

        if (result != null)
        {
            // Write UTF-8 bytes directly: works with or without a console attached.
            byte[] bytes = new UTF8Encoding(false).GetBytes(result + "\n");
            using (Stream stdout = Console.OpenStandardOutput())
            {
                stdout.Write(bytes, 0, bytes.Length);
            }
        }
        return 0;
    }
}
