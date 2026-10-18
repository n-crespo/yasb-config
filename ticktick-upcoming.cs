// ticktick-upcoming.cs
//
// Prints the TickTick task in progress right now (or the next one to start) as JSON:
//   {"state":"upcoming","title":"Lecture","time":"2-4pm","date":"","priority":"none",
//    "all_day":false,"start":"2026-10-18T14:00:00","end":"2026-10-18T16:00:00",
//    "label":"2-4pm: Lecture","details":"<b>Today</b><br>..."}
// "date" is a short weekday like "Tue" when the task isn't today, and is then also prefixed
// to "label". When there is nothing upcoming: {"state":"none","label":"No upcoming tasks."}
//
// "details" is an HTML list of every open task still to come in the next 3 days (DetailsDays),
// grouped by day (for a tooltip); "&nbsp;" is used for indentation.
//
// Timed tasks win over all-day tasks on the same day, but an all-day task beats timed tasks
// on later days. Looks 7 days ahead (optional argument: a different number of days).
// Tasks are ordered by START time; TickTick's dueDate is really the END of the duration.
//
// Needs the TickTick CLI ("ticktick") on PATH. Non-ASCII characters are written as \uXXXX
// escapes, so the output is plain ASCII.
//
// Written for the C# 5 compiler that ships with Windows (no C# 6 syntax).
//
// Build once, no installs needed:
//   C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe /nologo /optimize /r:System.Web.Extensions.dll /out:ticktick-upcoming.exe ticktick-upcoming.cs

using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

static class TickUpcoming
{
    const string CliName = "ticktick";
    const int DefaultLookaheadDays = 7;
    const int StatusOpen = 0;

    class Task
    {
        public string Title;
        public string Priority;
        public bool IsAllDay;
        public DateTime Start;
        public DateTime End;
        public int CliOrder;
    }

    // TickTick priority codes: 0 none, 1 low, 3 medium, 5 high.
    static string PriorityName(int priority)
    {
        switch (priority)
        {
            case 1: return "low";
            case 3: return "medium";
            case 5: return "high";
            default: return "none";
        }
    }

    // TickTick's "+0000" offset needs a colon before .NET will parse it.
    static DateTime? ParseLocal(object value)
    {
        string text = value as string;
        if (string.IsNullOrEmpty(text)) return null;
        text = Regex.Replace(text, @"([+-]\d{2})(\d{2})$", "$1:$2");
        return DateTimeOffset.Parse(text, CultureInfo.InvariantCulture).LocalDateTime;
    }

    static string UtcIso(DateTime local)
    {
        return local.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
    }

    static string RunCli(string arguments)
    {
        var info = new ProcessStartInfo(CliName, arguments);
        info.UseShellExecute = false;
        info.CreateNoWindow = true;
        info.RedirectStandardOutput = true;
        info.StandardOutputEncoding = Encoding.UTF8;

        using (Process process = Process.Start(info))
        {
            string output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            if (process.ExitCode != 0) throw new Exception("ticktick exited with code " + process.ExitCode);
            return output;
        }
    }

    // Open tasks that START within [from, to), in the CLI's own order.
    // The CLI can only filter on dueDate, so we query one extra day past the range
    // (to catch tasks that start late and end after midnight) and filter on start locally.
    static List<Task> GetTasks(DateTime from, DateTime to)
    {
        string arguments = "task filter" +
            " --start-date " + UtcIso(from) +
            " --end-date " + UtcIso(to.AddDays(1)) +
            " --status=" + StatusOpen + " --json";

        string json = RunCli(arguments);
        var tasks = new List<Task>();
        if (string.IsNullOrWhiteSpace(json)) return tasks;

        var serializer = new JavaScriptSerializer();
        var raw = serializer.DeserializeObject(json) as object[];
        if (raw == null) return tasks;

        int order = 0;
        foreach (object item in raw)
        {
            var dict = item as Dictionary<string, object>;
            if (dict == null) continue;

            DateTime? end = ParseLocal(Get(dict, "dueDate"));
            DateTime? start = ParseLocal(Get(dict, "startDate")) ?? end;
            if (start == null) continue;
            if (start.Value < from || start.Value >= to) continue;

            object priority = Get(dict, "priority");
            tasks.Add(new Task
            {
                Title = Get(dict, "title") as string ?? "",
                Priority = PriorityName(priority == null ? 0 : Convert.ToInt32(priority)),
                IsAllDay = Get(dict, "isAllDay") as bool? ?? false,
                Start = start.Value,
                End = end ?? start.Value,
                CliOrder = order++
            });
        }
        return tasks;
    }

    static object Get(Dictionary<string, object> dict, string key)
    {
        object value;
        return dict.TryGetValue(key, out value) ? value : null;
    }

    // Timed candidates are tasks in progress right now or yet to start; the best is the
    // earliest by start. An all-day task only beats it if it falls on an EARLIER DAY.
    // Among several all-day tasks, the first in the CLI's order wins.
    static Task SelectUpcoming(List<Task> tasks, DateTime now)
    {
        Task bestTimed = null;
        foreach (Task t in tasks)
        {
            if (t.IsAllDay) continue;
            bool inProgress = t.End != t.Start && t.Start <= now && now < t.End;
            if (!(t.Start > now || inProgress)) continue;
            if (bestTimed == null || t.Start < bestTimed.Start) bestTimed = t;
        }

        Task allDay = null;
        foreach (Task t in tasks)
        {
            if (!t.IsAllDay) continue;
            if (bestTimed != null && t.Start.Date >= bestTimed.Start.Date) continue;
            if (allDay == null || t.CliOrder < allDay.CliOrder) allDay = t;
        }

        return allDay ?? bestTimed;
    }

    // 2:00 PM -> "2pm", 2:30 PM -> "2:30pm". omitMeridiem drops the am/pm suffix.
    static string CompactTime(DateTime time, bool omitMeridiem)
    {
        string text = time.Minute == 0
            ? time.ToString("%h", CultureInfo.InvariantCulture)
            : time.ToString("h\\:mm", CultureInfo.InvariantCulture);
        if (!omitMeridiem) text += time.ToString("tt", CultureInfo.InvariantCulture).ToLowerInvariant();
        return text;
    }

    // "2-4pm", "10am-12pm", "2:30-4pm", "6:30pm" (no duration), or "all day".
    static string CompactRange(Task task)
    {
        if (task.IsAllDay) return "all day";
        if (task.End == task.Start) return CompactTime(task.Start, false);

        // Only the second time carries am/pm when both are in the same half of the day.
        bool sameMeridiem = (task.Start.Hour >= 12) == (task.End.Hour >= 12);
        return CompactTime(task.Start, sameMeridiem) + "-" + CompactTime(task.End, false);
    }

    // Escapes a string for use inside a JSON string literal (non-ASCII becomes \uXXXX).
    static string JsonEscape(string value)
    {
        var sb = new StringBuilder();
        foreach (char c in value)
        {
            if (c == '"')       sb.Append("\\\"");
            else if (c == '\\') sb.Append("\\\\");
            else if (c < 0x20 || c > 0x7E) sb.Append("\\u").Append(((int)c).ToString("x4"));
            else                sb.Append(c);
        }
        return sb.ToString();
    }

    static string Str(string value)
    {
        return "\"" + JsonEscape(value) + "\"";
    }

    static string HtmlEscape(string value)
    {
        return value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
    }

    // Foreground color per priority (any CSS color works). Null = default text color.
    static string PriorityColor(string priority)
    {
        switch (priority)
        {
            case "low":    return "#5b9bf0";
            case "medium": return "#e5b84d";
            case "high":   return "#e5484d";
            default:       return null;
        }
    }

    // The details list covers today plus the following days, up to this many days in total.
    const int DetailsDays = 3;

    const string MutedColor = "#8a8a8a";   // times and day dates
    const string FaintColor = "#4a4a4a";   // rules and the no-priority bar

    // Titles longer than this are cut short with an ellipsis so one long task can't stretch the tooltip.
    const int MaxTitleLength = 40;

    static string Truncate(string text)
    {
        return text.Length <= MaxTitleLength ? text : text.Substring(0, MaxTitleLength - 1).TrimEnd() + "…";
    }

    static string Span(string color, string text)
    {
        return "<span style=\"color:" + color + "\">" + text + "</span>";
    }

    // Width in pixels of the time column. Fixed so the bars and titles line up across days
    // (each day is its own table, because a wide heading cell would otherwise stretch the
    // columns of a shared table and open a gap after the time).
    const int TimeColumnWidth = 80;

    // Open tasks still to come (or in progress), left-aligned. Each day is a bold heading
    // followed by rows of time, a thin bar colored by priority, and the title.
    static string DetailsHtml(List<Task> tasks, DateTime now)
    {
        var upcoming = tasks.FindAll(delegate(Task t)
        {
            return t.Start.Date < now.Date.AddDays(DetailsDays) &&
                   (t.IsAllDay || t.Start > now || t.End > now);
        });
        if (upcoming.Count == 0) return "No upcoming tasks.";

        // By day; all-day tasks first within a day (in the CLI's order), then by start time.
        upcoming.Sort(delegate(Task a, Task b)
        {
            int byDay = a.Start.Date.CompareTo(b.Start.Date);
            if (byDay != 0) return byDay;
            if (a.IsAllDay != b.IsAllDay) return a.IsAllDay ? -1 : 1;
            int byStart = a.Start.CompareTo(b.Start);
            return byStart != 0 ? byStart : a.CliOrder.CompareTo(b.CliOrder);
        });

        var sb = new StringBuilder();
        DateTime day = DateTime.MinValue;
        foreach (Task t in upcoming)
        {
            if (t.Start.Date != day)
            {
                day = t.Start.Date;
                bool isRelative = day == now.Date || day == now.Date.AddDays(1);
                string heading = day == now.Date ? "Today"
                    : day == now.Date.AddDays(1) ? "Tomorrow"
                    : day.ToString("dddd", CultureInfo.InvariantCulture);
                if (sb.Length > 0) sb.Append("</table><br><br>");
                sb.Append("<b>").Append(heading).Append("</b>&nbsp;&nbsp;")
                  .Append(Span(MutedColor, day.ToString(isRelative ? "dddd, MMM d" : "MMM d", CultureInfo.InvariantCulture)))
                  .Append("<table cellspacing=\"0\" cellpadding=\"2\">");
            }

            string bar = PriorityColor(t.Priority) ?? FaintColor;
            sb.Append("<tr><td width=\"" + TimeColumnWidth + "\" align=\"right\">").Append(Span(MutedColor, HtmlEscape(CompactRange(t)))).Append("</td>")
              .Append("<td>&nbsp;").Append(Span(bar, "▎")).Append("&nbsp;</td>")
              .Append("<td>").Append(HtmlEscape(Truncate(t.Title.Trim()))).Append("</td></tr>");
        }
        return sb.Append("</table>").ToString();
    }

    static string ToJson(Task task, List<Task> tasks, DateTime now)
    {
        string time = CompactRange(task);
        string date = task.Start.Date != now.Date
            ? task.Start.ToString("ddd", CultureInfo.InvariantCulture) : "";
        string label = (date != "" ? date + " " : "") + time + ": " + task.Title;
        string iso = "yyyy-MM-dd'T'HH:mm:ss";

        return "{\"state\":\"upcoming\"," +
               "\"title\":" + Str(task.Title) + "," +
               "\"time\":" + Str(time) + "," +
               "\"date\":" + Str(date) + "," +
               "\"priority\":" + Str(task.Priority) + "," +
               "\"all_day\":" + (task.IsAllDay ? "true" : "false") + "," +
               "\"start\":" + Str(task.Start.ToString(iso, CultureInfo.InvariantCulture)) + "," +
               "\"end\":" + Str(task.End.ToString(iso, CultureInfo.InvariantCulture)) + "," +
               "\"label\":" + Str(label) + "," +
               "\"details\":" + Str(DetailsHtml(tasks, now)) + "}";
    }

    static int Main(string[] args)
    {
        int days = DefaultLookaheadDays;
        if (args.Length > 0) int.TryParse(args[0], out days);
        if (days < 1) days = DefaultLookaheadDays;

        DateTime now = DateTime.Now;
        string result;
        try
        {
            List<Task> tasks = GetTasks(now.Date, now.Date.AddDays(days));
            Task next = SelectUpcoming(tasks, now);
            result = next != null ? ToJson(next, tasks, now)
                : "{\"state\":\"none\",\"label\":\"No upcoming tasks.\",\"details\":\"No upcoming tasks.\"}";
        }
        catch (Exception e)
        {
            result = "{\"state\":\"error\",\"label\":\"TickTick error\",\"error\":" + Str(e.Message) + "}";
        }

        // Write bytes directly: works with or without a console attached.
        byte[] bytes = new UTF8Encoding(false).GetBytes(result + "\n");
        using (Stream stdout = Console.OpenStandardOutput())
        {
            stdout.Write(bytes, 0, bytes.Length);
        }
        return 0;
    }
}
