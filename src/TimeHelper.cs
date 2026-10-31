using System;
using System.Globalization;
using System.Text;

namespace TodoClock
{
    public static class TimeHelper
    {
        private static readonly TimeZoneInfo Tehran = FindTehran();
        public static readonly PersianCalendar Pc = new PersianCalendar();

        public static readonly string[] FaMonths =
        {
            "فروردین", "اردیبهشت", "خرداد", "تیر", "مرداد", "شهریور",
            "مهر", "آبان", "آذر", "دی", "بهمن", "اسفند"
        };

        // indexed by DayOfWeek (Sunday = 0)
        public static readonly string[] FaWeekDays =
        {
            "یکشنبه", "دوشنبه", "سه شنبه", "چهارشنبه", "پنجشنبه", "جمعه", "شنبه"
        };

        private static TimeZoneInfo FindTehran()
        {
            foreach (var id in new[] { "Iran Standard Time", "Asia/Tehran" })
            {
                try { return TimeZoneInfo.FindSystemTimeZoneById(id); } catch { }
            }
            return TimeZoneInfo.CreateCustomTimeZone("Tehran", new TimeSpan(3, 30, 0), "Tehran", "Tehran");
        }

        public static DateTime Now
        {
            get
            {
                try { return TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, Tehran); }
                catch { return DateTime.UtcNow.AddHours(3.5); }
            }
        }

        public static string Fa(int n) => Fa(n.ToString(CultureInfo.InvariantCulture));
        public static string Fa2(int n) => Fa(n.ToString("00", CultureInfo.InvariantCulture));

        public static string Fa(string s)
        {
            var sb = new StringBuilder(s.Length);
            foreach (char c in s)
                sb.Append(c >= '0' && c <= '9' ? (char)('۰' + (c - '0')) : c);
            return sb.ToString();
        }

        public static string FaDate(DateTime d) =>
            $"{FaWeekDays[(int)d.DayOfWeek]} {Fa(Pc.GetDayOfMonth(d))} {FaMonths[Pc.GetMonth(d) - 1]} {Fa(Pc.GetYear(d))}";

        public static string FaShortDate(DateTime d) =>
            $"{Fa(Pc.GetDayOfMonth(d))} {FaMonths[Pc.GetMonth(d) - 1]}";

        /// <summary>Label for a due date chip: "امروز، ۱ آبان", "فردا، ۲ آبان" or "۵ آبان" (year added if not this year).</summary>
        public static string DueLabel(DateTime due)
        {
            var today = Now.Date;
            due = due.Date;
            string s = FaShortDate(due);
            if (Pc.GetYear(due) != Pc.GetYear(today)) s += " " + Fa(Pc.GetYear(due));
            if (due == today) return "امروز، " + s;
            if (due == today.AddDays(1)) return "فردا، " + s;
            if (due == today.AddDays(-1)) return "دیروز، " + s;
            return s;
        }

        public static string EnDate(DateTime d) =>
            d.ToString("dddd, d MMMM yyyy", CultureInfo.InvariantCulture);

        public static bool IsRtlText(string s)
        {
            if (string.IsNullOrEmpty(s)) return true;
            foreach (char c in s)
            {
                if ((c >= '\u0590' && c <= '\u08FF') || (c >= '\uFB1D' && c <= '\uFDFF') || (c >= '\uFE70' && c <= '\uFEFF'))
                    return true;
                if (char.IsLetter(c)) return false;
            }
            return true;
        }
    }
}
