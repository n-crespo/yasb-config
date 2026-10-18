// ticktick-focus.cs
//
// Prints the TickTick PWA's focus timer state as an HTML label, based on its window title:
//   "TickTick - 10:50 - Focusing"  ->  <b style="color:#39c559;...">icon</b> 10:50
//   "TickTick - 11:36 - Paused"    ->  <b style="color:#e5484d;...">icon</b> 11:36
// Prints nothing when no timer is running or paused. It's plain text, not JSON, on purpose:
// with return_format "string", YASB's hide_empty hides the whole widget on empty output.
//
// Optional argument: "focusing" or "paused" prints only when the timer is in that state
// (and nothing otherwise), e.g.  ticktick-focus.exe paused
//
// While a timer is running, each run waits (cheaply, re-reading one window title every 50ms,
// at most ~1.1s) for the displayed time to change, so output lines up with the real ticks.
// Paused or no timer: returns immediately, no polling.
//
// Output is UTF-8(the icons are Nerd Font characters).
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

    // Tag and color for the icon in the "html" field. "b" is the tag YASB's own custom widget
    // scripts use for inline colors; try "span" if you'd rather not have bold.
    const string IconTag       = "b";
    const string IconFontSize  = "12px";
    const string IconMarginTop = "0px";
    const string FocusingColor = "#39c559";   // green (any CSS color works)
    const string PausedColor   = "#e5484d";   // red

    // Paused icon (Nerd Font, U+F03E6).
    const string PausedIcon = "󰏤";

    // Running icon: a Nerd Font clock that steps once per second of the TIMER's seconds.
    // nf-md-clock_time_one_outline (U+F144B) through twelve_outline (U+F1456) are consecutive.
    const int ClockOneCodePoint = 0xF144B;

    // Set to true if the icon spins backwards (e.g. a countdown timer).
    static readonly bool CounterClockwise = false;

    // Timer seconds 0 -> 12 o'clock, 1 -> 1 o'clock, ... 11 -> 11 o'clock, 12 -> 12 o'clock again.
    // 60 is a multiple of 12, so the animation stays smooth across minute boundaries.
    static string ClockIcon(int second)
    {
        int frame = second % 12;                       // 0..11
        if (CounterClockwise) frame = (12 - frame) % 12;
        int offset = (frame + 11) % 12;                // 0 = "one", 11 = "twelve"
        return char.ConvertFromUtf32(ClockOneCodePoint + offset);
    }

    // Seconds part of a timer string: "10:50" -> 50, "1:10:50" -> 50.
    // Falls back to the wall-clock second if the text is unexpected.
    static int SecondsOf(string time)
    {
        int seconds;
        string last = time.Substring(time.LastIndexOf(':') + 1);
        if (int.TryParse(last, out seconds)) return seconds;
        return DateTime.Now.Second;
    }

    // The label as HTML: only the icon is wrapped in a colored tag; the time is left plain.
    static string ToHtml(string icon, string time, string color)
    {
        string style = "color:" + color + ";font-size:" + IconFontSize + ";margin-top:" + IconMarginTop;
        return "<" + IconTag + " style=\"" + style + "\">" + icon + "</" + IconTag + "> " + time;
    }

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

    // When a timer is running, block until its displayed time changes (or this long passes),
    // so each run returns right on a real tick instead of at YASB's arbitrary phase.
    const int MaxWaitMs = 1100;
    const int PollMs    = 50;

    // One scan of all top-level windows. Remembers the running timer's window handle so the
    // wait loop can re-read just that one title instead of enumerating every window.
    static void Snapshot(out string focusing, out string paused, out IntPtr focusWnd)
    {
        string f = null;
        string p = null;
        IntPtr fw = IntPtr.Zero;
        var buffer = new StringBuilder(256);

        EnumWindows(delegate(IntPtr hWnd, IntPtr lParam)
        {
            if (GetWindowText(hWnd, buffer, buffer.Capacity) == 0) return true;
            string title = buffer.ToString();

            string time = ParseTime(title, FocusingSuffix);
            if (time != null)
            {
                f = time;
                fw = hWnd;
                return false;   // a running timer wins; stop searching
            }

            if (p == null) p = ParseTime(title, PausedSuffix);
            return true;
        }, IntPtr.Zero);

        focusing = f;
        paused = p;
        focusWnd = fw;
    }

    // Polls one window's title until the time differs from start. Returns the new time, the
    // unchanged time on timeout, or null if the window is no longer in the focusing state.
    static string WaitForTick(IntPtr hWnd, string start)
    {
        var buffer = new StringBuilder(256);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (clock.ElapsedMilliseconds < MaxWaitMs)
        {
            System.Threading.Thread.Sleep(PollMs);
            if (GetWindowText(hWnd, buffer, buffer.Capacity) == 0) return null;
            string time = ParseTime(buffer.ToString(), FocusingSuffix);
            if (time == null) return null;
            if (time != start) return time;
        }
        return start;
    }

    static int Main(string[] args)
    {
        string focusing, paused;
        IntPtr focusWnd;
        Snapshot(out focusing, out paused, out focusWnd);

        // Only a running timer waits; paused / no timer return immediately without polling.
        if (focusing != null)
        {
            string next = WaitForTick(focusWnd, focusing);
            if (next != null) focusing = next;
            else Snapshot(out focusing, out paused, out focusWnd);   // state changed: rescan
        }

        // Optional argument ("focusing" or "paused"): only print when the timer is in
        // that state. Lets you run one YASB widget per state and style each in CSS.
        string only = args.Length > 0 ? args[0].ToLowerInvariant() : null;

        // A running timer wins over a paused one.
        string state = focusing != null ? "focusing" : (paused != null ? "paused" : null);

        string result = null;
        if (state != null && (only == null || only == state))
        {
            result = (state == "focusing")
                ? ToHtml(ClockIcon(SecondsOf(focusing)), focusing, FocusingColor)
                : ToHtml(PausedIcon, paused, PausedColor);
        }

        if (result == null) return 0;

        // Write bytes directly: works with or without a console attached.
        byte[] bytes = new UTF8Encoding(false).GetBytes(result + "\n");
        using (Stream stdout = Console.OpenStandardOutput())
        {
            stdout.Write(bytes, 0, bytes.Length);
        }
        return 0;
    }
}
