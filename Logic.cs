using System;
using System.Collections.Generic;
using System.Globalization;

namespace DesktopTodo
{
    // Pure presentation/state for a task deadline. Brushes stay in App.cs so this
    // file compiles without WPF and can be exercised by the console tests.
    //
    // Nothing in this file produces display text: callers get enum states and
    // DateTime values, and the UI layer formats them through Strings. That keeps
    // the pure layer language-neutral and stops tests from asserting on wording.
    public enum DueState { None, Future, Today, Overdue, Completed }

    public static class DueLogic
    {
        private static readonly string[] ParseFormats = { "yyyy-MM-dd'T'HH:mm", "yyyy-M-d'T'H:mm" };

        public static DateTime? Parse(string stored)
        {
            if (String.IsNullOrWhiteSpace(stored)) return null;
            DateTime value;
            if (!DateTime.TryParseExact(stored.Trim(), ParseFormats, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out value)) return null;
            // Deadlines are hour-precise; drop any hand-edited minutes/seconds.
            return new DateTime(value.Year, value.Month, value.Day, value.Hour, 0, 0);
        }

        public static string Format(DateTime due)
        {
            return due.ToString("yyyy-MM-dd'T'HH:00", CultureInfo.InvariantCulture);
        }

        // Canonical storage value: null for blank/invalid input, standard format otherwise.
        public static string Normalize(string stored)
        {
            DateTime? parsed = Parse(stored);
            return parsed.HasValue ? Format(parsed.Value) : null;
        }

        public static string FormatOrNull(DateTime? due)
        {
            return due.HasValue ? Format(due.Value) : null;
        }

        public static DueState State(DateTime? due, bool completed, DateTime now)
        {
            if (!due.HasValue) return DueState.None;
            if (completed) return DueState.Completed;
            DateTime value = due.Value;
            if (value < now) return DueState.Overdue;
            if (value.Date == now.Date) return DueState.Today;
            return DueState.Future;
        }

        // Which date bucket a deadline falls into, so the UI can pick a wording
        // ("today", "tomorrow", "18 September") without re-deriving the rule.
        public static DueDay Day(DateTime value, DateTime now)
        {
            if (value.Date == now.Date) return DueDay.Today;
            if (value.Date == now.Date.AddDays(1)) return DueDay.Tomorrow;
            return value.Year == now.Year ? DueDay.ThisYear : DueDay.OtherYear;
        }
    }

    public enum DueDay { Today, Tomorrow, ThisYear, OtherYear }

    // One cell of the month grid. Cells are plain data; XAML templates decide the look.
    public sealed class CalendarCell
    {
        public DateTime Date { get; set; }
        public string DayLabel { get; set; }
        public bool IsToday { get; set; }
        public bool IsCurrentMonth { get; set; }
        public int Count { get; set; }        // due count in dot mode, completions in heat mode
        public int HeatLevel { get; set; }    // 0-4 in normal heat mode, -1 when out of scale
        public bool IsHoliday { get; set; }
        public bool IsWeekend { get; set; }
        // The heart day. The cell shows a glyph instead of the date.
        public bool IsAnniversary { get; set; }
        public string HolidayCode { get; set; }   // stable identity, localised by the UI
        public string HolidayName { get; set; }    // localised name for the active language
        // A red dot is drawn for gazetted holidays and plain weekends alike.
        public bool HasMarker { get { return IsHoliday || IsWeekend; } }
        // What that dot means: the holiday's own name, or "weekend". A holiday wins
        // when it lands on a Saturday or Sunday.
        public string MarkerName { get; set; }
        public string ToolTipText { get; set; }
    }

    // One cell's heat state: the level plus what it means, so the UI can pick a
    // colour and a wording without re-deriving either.
    public sealed class HeatCellInfo
    {
        public int Level;        // 0-4, or -1 for "out of scale" (a future day)
        public bool InScale;     // false when the day cannot be judged yet
        public int Completed;    // completions logged that day
        public bool IsFuture;    // later than "today"
    }

    public static class CalendarBuilder
    {
        // The one date a year that turns into a heart. Kept here rather than in the
        // holiday table because it is a private keepsake, not a public holiday.
        public const int AnniversaryMonth = 8;
        public const int AnniversaryDay = 17;

        // Always 42 cells (6 weeks), weeks start on Monday to match the zh-CN header.
        public static List<CalendarCell> BuildCells(int year, int month, DateTime today,
            IDictionary<DateTime, int> dueCounts, IDictionary<DateTime, int> doneCounts,
            IDictionary<DateTime, List<string>> doneTexts, bool heatMode,
            bool invertHeat, UiLanguage language)
        {
            DateTime first = new DateTime(year, month, 1);
            int offset = ((int)first.DayOfWeek + 6) % 7; // Monday -> 0
            DateTime start = first.AddDays(-offset);
            // Both colour directions judge elapsed days only. A day that has not
            // happened yet can never have been completed, so letting it set the ceiling
            // would push every real day down the scale - or, when a future day is the
            // only one carrying completions, flatten two days onto a single shade.
            int maxDone = MaxInView(doneCounts, start, today);

            List<CalendarCell> cells = new List<CalendarCell>(42);
            for (int i = 0; i < 42; i++)
            {
                DateTime date = start.AddDays(i).Date;
                int due = dueCounts != null && dueCounts.ContainsKey(date) ? dueCounts[date] : 0;
                int done = doneCounts != null && doneCounts.ContainsKey(date) ? doneCounts[date] : 0;
                string holidayName = HolidayData.Name(date, language);
                // Weekends carry the same red marker as gazetted holidays. A holiday
                // that lands on a Saturday or Sunday keeps its own name; a plain
                // weekend is labelled "weekend" instead.
                bool isWeekend = date.DayOfWeek == DayOfWeek.Saturday || date.DayOfWeek == DayOfWeek.Sunday;
                bool isAnniversary = date.Month == AnniversaryMonth && date.Day == AnniversaryDay;
                string markerName = holidayName != null
                    ? holidayName
                    : (isWeekend ? Strings.T(language, "holiday.weekend") : null);
                // In heat mode the level decides the colour; in dot mode it stays 0 and
                // is ignored. In inverted mode a future day reports -1 ("out of scale")
                // and the UI leaves it unpainted rather than colouring it as a failure.
                int heatLevel = 0;
                if (heatMode)
                {
                    HeatCellInfo heat = HeatFor(date, today, done, maxDone, invertHeat);
                    heatLevel = heat.InScale ? heat.Level : -1;
                }
                CalendarCell cell = new CalendarCell
                {
                    Date = date,
                    // One day a year the number gives way to a heart.
                    DayLabel = isAnniversary
                        ? Strings.T(language, "anniversary.glyph")
                        : date.Day.ToString(CultureInfo.InvariantCulture),
                    IsToday = date == today.Date,
                    IsCurrentMonth = date.Month == month,
                    Count = heatMode ? done : due,
                    HeatLevel = heatLevel,
                    IsHoliday = holidayName != null,
                    IsWeekend = isWeekend,
                    IsAnniversary = isAnniversary,
                    HolidayCode = holidayName != null ? HolidayCode(date) : null,
                    HolidayName = holidayName,
                    MarkerName = markerName,
                    ToolTipText = isAnniversary
                        ? AnniversaryToolTip(heatMode
                            ? HeatToolTip(date, done, doneTexts, language, markerName)
                            : DueToolTip(date, due, markerName, language), language)
                        : heatMode
                            ? HeatToolTip(date, done, doneTexts, language, markerName)
                            : DueToolTip(date, due, markerName, language)
                };
                cells.Add(cell);
            }
            return cells;
        }

        // Highest completion count inside the visible window. Counting stops at
        // `lastCountedDay` so that days still in the future cannot drag the ceiling
        // up or down for the days that have already been lived.
        private static int MaxInView(IDictionary<DateTime, int> counts, DateTime start, DateTime lastCountedDay)
        {
            int max = 0;
            if (counts == null) return 0;
            for (int i = 0; i < 42; i++)
            {
                DateTime date = start.AddDays(i).Date;
                if (date > lastCountedDay.Date) break;
                int count;
                if (counts.TryGetValue(date, out count) && count > max) max = count;
            }
            return max;
        }

        // Heat level for one day. Inverted mode needs "today" so that a day which
        // cannot have been completed yet is excluded from the scale instead of being
        // painted as a complete failure - and in inverted mode a failure is the
        // deepest shade there is, which is exactly why a future day must opt out.
        public static HeatCellInfo HeatFor(DateTime date, DateTime today, int completed, int maxCompleted, bool inverted)
        {
            bool future = date.Date > today.Date;
            HeatCellInfo info = new HeatCellInfo { Completed = completed, IsFuture = future };
            if (inverted && future)
            {
                info.InScale = false;
                info.Level = -1;
                return info;
            }
            info.InScale = true;
            info.Level = HeatScale.Level(completed, maxCompleted, inverted);
            return info;
        }

        private static string DueToolTip(DateTime date, int due, string markerName, UiLanguage language)
        {
            string label = Strings.Date(language, date);
            if (markerName != null) label = Strings.T(language, "cal.tip.holiday", label, markerName);
            return due > 0 ? label + " · " + Strings.CountOf(language, due, "due") : label;
        }

        // The keepsake line leads, and the ordinary date/deadline detail follows on a
        // second line rather than being dropped - the tooltip is the only place the
        // real date is still spelled out once the cell shows a heart.
        private static string AnniversaryToolTip(string detail, UiLanguage language)
        {
            return Strings.T(language, "anniversary.tip") + "\n" + detail;
        }

        private static string HeatToolTip(DateTime date, int done, IDictionary<DateTime, List<string>> doneTexts, UiLanguage language, string markerName)
        {
            string label = Strings.Date(language, date);
            if (markerName != null) label = Strings.T(language, "cal.tip.holiday", label, markerName);
            if (done == 0) return Strings.T(language, "cal.tip.noDone", label);
            string tip = label + " · " + Strings.CountOf(language, done, "done");
            List<string> texts;
            if (doneTexts != null && doneTexts.TryGetValue(date, out texts) && texts != null)
            {
                int shown = 0;
                foreach (string text in texts)
                {
                    if (shown >= 3) break;
                    string line = (text ?? String.Empty).Replace('\r', ' ').Replace('\n', ' ').Trim();
                    if (line.Length == 0) continue;
                    if (line.Length > 20) line = line.Substring(0, 20) + "…";
                    tip += "\n· " + line;
                    shown++;
                }
                if (done > shown) tip += "\n" + Strings.T(language, "cal.tip.more", done - shown);
            }
            return tip;
        }

        // Stable per-day identity for a holiday, so the UI can re-derive the name
        // in whatever language is active instead of caching a translated string.
        private static string HolidayCode(DateTime date)
        {
            return date.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        }

        public static DateTime HolidayDate(string code)
        {
            DateTime value;
            return !String.IsNullOrEmpty(code)
                && DateTime.TryParseExact(code, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out value)
                ? value : DateTime.MinValue;
        }
    }

    public static class HeatScale
    {
        // Near-white to theme green, matching the card palette.
        public static readonly string[] Colors = { "#EDF1E8", "#D5E8DA", "#A8D3B4", "#6FB386", "#3C7865" };

        // In inverted mode the palette is walked end to end: the busiest day takes the
        // lightest shade and a day with nothing completed takes the deepest, so "no
        // progress" becomes the loudest colour on the grid.
        public static int Level(int count, int maxCount)
        {
            return Level(count, maxCount, false);
        }

        public static int Level(int count, int maxCount, bool inverted)
        {
            int level = ForwardLevel(count, maxCount);
            return inverted ? Colors.Length - 1 - level : level;
        }

        private static int ForwardLevel(int count, int maxCount)
        {
            if (count <= 0 || maxCount <= 0) return 0;
            double ratio = (double)count / maxCount;
            if (ratio <= 0.25) return 1;
            if (ratio <= 0.5) return 2;
            if (ratio <= 0.75) return 3;
            return 4;
        }
    }

    // One reminder slot: which hour it fires at, addressed by a stable kind so the
    // morning and the afternoon reminder keep independent "last fired" records.
    public sealed class ReminderSlot
    {
        public const string MorningKind = "morning";
        public const string EveningKind = "evening";
        // The two slots may not land on the same hour, and neither may sit outside
        // the allowed window.
        public const int FloorHour = 6;
        public const int CeilingHour = 22;

        public readonly string Kind;
        public readonly int Hour;

        public ReminderSlot(string kind, int hour)
        {
            Kind = kind;
            Hour = hour;
        }

        public static ReminderSlot Morning(int hour) { return new ReminderSlot(MorningKind, hour); }
        public static ReminderSlot Evening(int hour) { return new ReminderSlot(EveningKind, hour); }

        public const int DefaultMorningHour = 9;
        public const int DefaultEveningHour = 16;

        public static bool IsValidHour(int hour)
        {
            return hour >= FloorHour && hour <= CeilingHour;
        }

        // Repairs a stored pair: an out-of-range hour falls back to its default, and
        // two slots landing on the same hour push the evening one to its default.
        public static void Repair(ref int morningHour, ref int eveningHour)
        {
            if (!IsValidHour(morningHour)) morningHour = DefaultMorningHour;
            if (!IsValidHour(eveningHour)) eveningHour = DefaultEveningHour;
            if (morningHour == eveningHour) eveningHour = DefaultEveningHour;
            if (morningHour == eveningHour) morningHour = DefaultMorningHour;
        }
    }

    public static class ReminderLogic
    {
        public static string DayKey(DateTime value)
        {
            return value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }

        public static bool IsValidDayKey(string stored)
        {
            if (String.IsNullOrWhiteSpace(stored)) return false;
            DateTime value;
            return DateTime.TryParseExact(stored.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out value);
        }

        // Unfinished tasks due on a specific calendar day, sorted by hour.
        public static List<TodoRecord> DuesOn(IEnumerable<TodoRecord> tasks, DateTime day)
        {
            DateTime target = day.Date;
            List<TodoRecord> result = new List<TodoRecord>();
            if (tasks == null) return result;
            foreach (TodoRecord task in tasks)
            {
                if (task == null || task.IsCompleted) continue;
                DateTime? due = DueLogic.Parse(task.DueAt);
                if (due.HasValue && due.Value.Date == target) result.Add(task);
            }
            SortByDue(result);
            return result;
        }

        // "Tomorrow" is the next local calendar day; only unfinished tasks with a due date then.
        public static List<TodoRecord> TomorrowDues(IEnumerable<TodoRecord> tasks, DateTime now)
        {
            return DuesOn(tasks, now.Date.AddDays(1));
        }

        // The 09:00 slot reports what is due today.
        public static List<TodoRecord> TodayDues(IEnumerable<TodoRecord> tasks, DateTime now)
        {
            return DuesOn(tasks, now.Date);
        }

        public static List<TodoRecord> Overdue(IEnumerable<TodoRecord> tasks, DateTime now)
        {
            List<TodoRecord> result = new List<TodoRecord>();
            if (tasks == null) return result;
            foreach (TodoRecord task in tasks)
            {
                if (task == null || task.IsCompleted) continue;
                DateTime? due = DueLogic.Parse(task.DueAt);
                if (due.HasValue && due.Value < now) result.Add(task);
            }
            SortByDue(result);
            return result;
        }

        private static void SortByDue(List<TodoRecord> rows)
        {
            rows.Sort(delegate (TodoRecord a, TodoRecord b)
            {
                return DueLogic.Parse(a.DueAt).Value.CompareTo(DueLogic.Parse(b.DueAt).Value);
            });
        }

        // >= rather than == on the hour: an app started after the configured hour
        // must not miss that day's reminder.
        public static bool ShouldRemind(string lastRemindedDayKey, int reminderHour, DateTime now, bool hasDues)
        {
            if (!hasDues) return false;
            if (now.Hour < reminderHour) return false;
            return lastRemindedDayKey != DayKey(now);
        }

        // The dedupe record is per slot so the morning and the afternoon reminder are
        // tracked independently ("morning@2026-09-20").
        public static string SlotKey(string kind, DateTime day)
        {
            return kind + "@" + DayKey(day);
        }

        public static bool IsSlotKey(string stored, string kind)
        {
            if (String.IsNullOrWhiteSpace(stored) || String.IsNullOrEmpty(kind)) return false;
            string prefix = kind + "@";
            return stored.Trim().StartsWith(prefix, StringComparison.Ordinal)
                && IsValidDayKey(stored.Trim().Substring(prefix.Length));
        }

        public static DateTime SlotDay(string stored, string kind)
        {
            if (!IsSlotKey(stored, kind)) return DateTime.MinValue;
            DateTime value;
            DateTime.TryParseExact(stored.Trim().Substring(kind.Length + 1), "yyyy-MM-dd",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out value);
            return value;
        }

        public static bool ShouldFireSlot(string storedKey, string kind, int hour, DateTime now, bool hasDues)
        {
            if (!hasDues) return false;
            if (now.Hour < hour) return false;
            return SlotDay(storedKey, kind) != now.Date;
        }
    }

    // Which rolling window the restore picker filters archived clearings by. The
    // order is the combo order in the restore window; Day..Year use WindowDays.
    public enum RestoreRange { Day = 0, Week = 1, Month = 2, Year = 3, All = 4, ExactDate = 5 }

    // Pure selection logic for "restore to-do": dedupe the archive by id (latest
    // wins), drop ids already back in the list, then let the caller compose the
    // time window, the exact date and the text filter. No WPF dependency, so the
    // console tests exercise every rule directly.
    public static class RestoreLogic
    {
        // Rolling window length in days per RestoreRange; All and ExactDate do not
        // use the value (All is always true, ExactDate is judged by ExactMatch).
        public static readonly double[] WindowDays = { 1, 7, 30, 365, 0, 0 };

        // age = now - clearedAt <= window, boundary included. A clearing exactly
        // 24 hours ago still counts as "last day"; one second older does not.
        public static bool InWindow(RestoreRange range, DateTime clearedAt, DateTime now)
        {
            if (range == RestoreRange.All) return true;
            if (range == RestoreRange.ExactDate) return false;
            double window = WindowDays[(int)range];
            return now - clearedAt <= TimeSpan.FromDays(window);
        }

        // "Exact date" means the local calendar day [day 00:00, day+1 00:00).
        public static bool ExactMatch(DateTime clearedAt, DateTime day)
        {
            DateTime start = day.Date;
            return clearedAt >= start && clearedAt < start.AddDays(1);
        }

        // Latest record per id, then drop ids that are already back in the task
        // list. Result is ordered newest-clearing-first. Time and text filters are
        // deliberately left to the caller so this stays reusable for the tray count.
        public static List<ClearedEntry> Restorable(IEnumerable<ClearedEntry> entries, HashSet<string> currentTaskIds, DateTime now)
        {
            Dictionary<string, ClearedEntry> latest = new Dictionary<string, ClearedEntry>(StringComparer.Ordinal);
            if (entries != null)
            {
                foreach (ClearedEntry entry in entries)
                {
                    if (entry == null || String.IsNullOrEmpty(entry.Id)) continue;
                    // Append order is file order; a later line always wins.
                    latest[entry.Id] = entry;
                }
            }
            List<ClearedEntry> result = new List<ClearedEntry>();
            foreach (ClearedEntry entry in latest.Values)
            {
                if (currentTaskIds != null && currentTaskIds.Contains(entry.Id)) continue;
                result.Add(entry);
            }
            result.Sort(delegate (ClearedEntry a, ClearedEntry b)
            {
                int order = b.ClearedAt.CompareTo(a.ClearedAt);
                if (order != 0) return order;
                return String.CompareOrdinal(a.Id, b.Id);
            });
            return result;
        }

        // Case-insensitive substring over the task text. Null/empty keyword matches
        // everything; null text only matches an empty keyword.
        public static bool TextMatches(string text, string keyword)
        {
            if (String.IsNullOrEmpty(keyword)) return true;
            if (text == null) return false;
            return text.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
