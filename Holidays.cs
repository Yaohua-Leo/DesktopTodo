using System;
using System.Collections.Generic;
using System.Globalization;

namespace DesktopTodo
{
    // Hong Kong general holidays (公眾假期) - the 17 gazetted days per year.
    //
    // 2026-2027 come from the Government's own machine-readable feed
    // (1823.gov.hk iCal, cross-checked against gov.hk's gazetted page). 2025 is
    // transcribed from that gazetted page because the feed no longer carries it.
    // The 2028 list is not gazetted yet, so those dates are derived from the 2027
    // substitution rules and were cross-checked against two independent published
    // holiday calendars; re-import before 2029.
    //
    // Each row is: yyyyMMdd, weekday (0=Monday .. 6=Sunday), then one name per
    // language in UiLanguage order (zh-Hans | zh-Hant | en). 1900-01-01 was a
    // Monday, which anchors the weekday cross-check below.
    public static class HolidayData
    {
        private static readonly DateTime Epoch = new DateTime(1900, 1, 1);

        public static int FirstYear { get { return 2025; } }
        public static int LastYear { get { return 2028; } }
        public static int DayCount { get { return Table().Count; } }

        private const string Raw =
            "20250101 2 一月一日|一月一日|The first day of January"
            + "\n" + "20250129 2 农历年初一|農曆年初一|Lunar New Year’s Day"
            + "\n" + "20250130 3 农历年初二|農曆年初二|The second day of Lunar New Year"
            + "\n" + "20250131 4 农历年初三|農曆年初三|The third day of Lunar New Year"
            + "\n" + "20250404 4 清明节|清明節|Ching Ming Festival"
            + "\n" + "20250418 4 耶稣受难节|耶穌受難節|Good Friday"
            + "\n" + "20250419 5 耶稣受难节翌日|耶穌受難節翌日|The day following Good Friday"
            + "\n" + "20250421 0 复活节星期一|復活節星期一|Easter Monday"
            + "\n" + "20250501 3 劳动节|勞動節|Labour Day"
            + "\n" + "20250505 0 佛诞|佛誕|The Birthday of the Buddha"
            + "\n" + "20250531 5 端午节|端午節|Tuen Ng Festival"
            + "\n" + "20250701 1 香港特别行政区成立纪念日|香港特別行政區成立紀念日|Hong Kong Special Administrative Region Establishment Day"
            + "\n" + "20251001 2 国庆日|國慶日|National Day"
            + "\n" + "20251007 1 中秋节翌日|中秋節翌日|The day following the Chinese Mid-Autumn Festival"
            + "\n" + "20251029 2 重阳节|重陽節|Chung Yeung Festival"
            + "\n" + "20251225 3 圣诞节|聖誕節|Christmas Day"
            + "\n" + "20251226 4 圣诞节后第一个周日|聖誕節後第一個周日|The first weekday after Christmas Day"
            + "\n" + "20260101 3 一月一日|一月一日|The first day of January"
            + "\n" + "20260217 1 农历年初一|農曆年初一|Lunar New Year’s Day"
            + "\n" + "20260218 2 农历年初二|農曆年初二|The second day of Lunar New Year"
            + "\n" + "20260219 3 农历年初三|農曆年初三|The third day of Lunar New Year"
            + "\n" + "20260403 4 耶稣受难节|耶穌受難節|Good Friday"
            + "\n" + "20260404 5 耶稣受难节翌日|耶穌受難節翌日|The day following Good Friday"
            + "\n" + "20260406 0 清明节翌日|清明節翌日|The day following Ching Ming Festival"
            + "\n" + "20260407 1 复活节星期一翌日|復活節星期一翌日|The day following Easter Monday"
            + "\n" + "20260501 4 劳动节|勞動節|Labour Day"
            + "\n" + "20260525 0 佛诞翌日|佛誕翌日|The day following the Birthday of the Buddha"
            + "\n" + "20260619 4 端午节|端午節|Tuen Ng Festival"
            + "\n" + "20260701 2 香港特别行政区成立纪念日|香港特別行政區成立紀念日|Hong Kong Special Administrative Region Establishment Day"
            + "\n" + "20260926 5 中秋节翌日|中秋節翌日|The day following the Chinese Mid-Autumn Festival"
            + "\n" + "20261001 3 国庆日|國慶日|National Day"
            + "\n" + "20261019 0 重阳节翌日|重陽節翌日|The day following Chung Yeung Festival"
            + "\n" + "20261225 4 圣诞节|聖誕節|Christmas Day"
            + "\n" + "20261226 5 圣诞节后第一个周日|聖誕節後第一個周日|The first weekday after Christmas Day"
            + "\n" + "20270101 4 一月一日|一月一日|The first day of January"
            + "\n" + "20270206 5 农历年初一|農曆年初一|Lunar New Year's Day"
            + "\n" + "20270208 0 农历年初三|農曆年初三|The third day of Lunar New Year"
            + "\n" + "20270209 1 农历年初四|農曆年初四|The fourth day of Lunar New Year"
            + "\n" + "20270326 4 耶稣受难节|耶穌受難節|Good Friday"
            + "\n" + "20270327 5 耶稣受难节翌日|耶穌受難節翌日|The day following Good Friday"
            + "\n" + "20270329 0 复活节星期一|復活節星期一|Easter Monday"
            + "\n" + "20270405 0 清明节|清明節|Ching Ming Festival"
            + "\n" + "20270501 5 劳动节|勞動節|Labour Day"
            + "\n" + "20270513 3 佛诞|佛誕|The Birthday of the Buddha"
            + "\n" + "20270609 2 端午节|端午節|Tuen Ng Festival"
            + "\n" + "20270701 3 香港特别行政区成立纪念日|香港特別行政區成立紀念日|Hong Kong Special Administrative Region Establishment Day"
            + "\n" + "20270916 3 中秋节翌日|中秋節翌日|The day following the Chinese Mid-Autumn Festival"
            + "\n" + "20271001 4 国庆日|國慶日|National Day"
            + "\n" + "20271008 4 重阳节|重陽節|Chung Yeung Festival"
            + "\n" + "20271225 5 圣诞节|聖誕節|Christmas Day"
            + "\n" + "20271227 0 圣诞节后第一个周日|聖誕節後第一個周日|The first weekday after Christmas Day"
            + "\n" + "20280101 5 一月一日|一月一日|The first day of January"
            + "\n" + "20280126 2 农历年初一|農曆年初一|Lunar New Year’s Day"
            + "\n" + "20280127 3 农历年初二|農曆年初二|The second day of Lunar New Year"
            + "\n" + "20280128 4 农历年初三|農曆年初三|The third day of Lunar New Year"
            + "\n" + "20280324 4 耶稣受难节|耶穌受難節|Good Friday"
            + "\n" + "20280325 5 耶稣受难节翌日|耶穌受難節翌日|The day following Good Friday"
            + "\n" + "20280327 0 复活节星期一|復活節星期一|Easter Monday"
            + "\n" + "20280404 1 清明节|清明節|Ching Ming Festival"
            + "\n" + "20280501 0 劳动节|勞動節|Labour Day"
            + "\n" + "20280502 1 佛诞|佛誕|The Birthday of the Buddha"
            + "\n" + "20280529 0 端午节|端午節|Tuen Ng Festival"
            + "\n" + "20280701 5 香港特别行政区成立纪念日|香港特別行政區成立紀念日|Hong Kong Special Administrative Region Establishment Day"
            + "\n" + "20281001 6 国庆日|國慶日|National Day"
            + "\n" + "20281002 0 国庆日翌日|國慶日翌日|The day following National Day"
            + "\n" + "20281004 2 中秋节翌日|中秋節翌日|The day following the Chinese Mid-Autumn Festival"
            + "\n" + "20281026 3 重阳节|重陽節|Chung Yeung Festival"
            + "\n" + "20281225 0 圣诞节|聖誕節|Christmas Day"
            + "\n" + "20281226 1 圣诞节后第一个周日|聖誕節後第一個周日|The first weekday after Christmas Day";

        private static Dictionary<DateTime, string[]> table;

        private static Dictionary<DateTime, string[]> Table()
        {
            if (table != null) return table;
            Dictionary<DateTime, string[]> parsed = new Dictionary<DateTime, string[]>();
            string[] rows = Raw.Split('\n');
            for (int index = 0; index < rows.Length; index++)
            {
                string row = rows[index].Trim();
                if (row.Length == 0) continue;
                // The payload is "yyyyMMdd weekday names", but the names may contain
                // spaces (English holiday names do), so split only on the first two
                // spaces and treat the remainder as the whole triple.
                int firstSpace = row.IndexOf(' ');
                if (firstSpace < 0) continue;
                int secondSpace = row.IndexOf(' ', firstSpace + 1);
                if (secondSpace < 0) continue;
                string stamp = row.Substring(0, firstSpace);
                string weekdayText = row.Substring(firstSpace + 1, secondSpace - firstSpace - 1);
                string payload = row.Substring(secondSpace + 1);
                if (stamp.Length != 8) continue;
                int year, month, day, weekday;
                if (!Int32.TryParse(stamp.Substring(0, 4), NumberStyles.None, CultureInfo.InvariantCulture, out year)
                    || !Int32.TryParse(stamp.Substring(4, 2), NumberStyles.None, CultureInfo.InvariantCulture, out month)
                    || !Int32.TryParse(stamp.Substring(6, 2), NumberStyles.None, CultureInfo.InvariantCulture, out day)
                    || !Int32.TryParse(weekdayText, NumberStyles.None, CultureInfo.InvariantCulture, out weekday)) continue;
                DateTime date;
                try { date = new DateTime(year, month, day); }
                catch (ArgumentOutOfRangeException) { continue; }
                string[] languages = payload.Split('|');
                if (languages.Length != 3) continue;
                // The stored weekday guards the anchors above: a row that disagrees
                // with the real calendar is skipped rather than trusted.
                if ((int)(date - Epoch).TotalDays % 7 != weekday) continue;
                parsed[date] = languages;
            }
            table = parsed;
            return table;
        }

        public static bool IsHoliday(DateTime date)
        {
            return Table().ContainsKey(date.Date);
        }

        public static bool Covers(DateTime date)
        {
            return date.Year >= FirstYear && date.Year <= LastYear;
        }

        // The localised holiday name, or null when the day is not a holiday.
        public static string Name(DateTime date, UiLanguage language)
        {
            string[] names;
            if (!Table().TryGetValue(date.Date, out names)) return null;
            string value = names[(int)language];
            return String.IsNullOrEmpty(value) ? null : value;
        }
    }
}
