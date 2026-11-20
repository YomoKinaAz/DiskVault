using System.Globalization;
using DiskVault.Storage;

namespace DiskVault.Jobs;

/// <summary>
/// スケジュール。増分は「毎日（曜日指定）の決まった時刻」か「N 時間ごと」、
/// フルは「毎週 X 曜日」か「毎月 N 日」。同じ時刻に重なったらフルを優先する。
/// </summary>
public sealed class ScheduleConfig
{
    public bool Enabled { get; set; } = true;
    /// <summary>"daily" / "hourly" / "none"</summary>
    public string Incremental { get; set; } = "daily";
    public List<DayOfWeek> Days { get; set; } = [.. Enum.GetValues<DayOfWeek>()];
    public List<string> Times { get; set; } = ["02:00"];
    public int IntervalHours { get; set; } = 6;
    public string HourlyStart { get; set; } = "00:00";
    /// <summary>"none" / "weekly" / "monthly"</summary>
    public string Full { get; set; } = "weekly";
    public DayOfWeek FullDay { get; set; } = DayOfWeek.Sunday;
    public int FullDayOfMonth { get; set; } = 1;
    public string FullTime { get; set; } = "02:00";
    /// <summary>電源オフなどで実行できなかった回を、次に起動したときに実行する。</summary>
    public bool RunMissed { get; set; } = true;

    static TimeSpan T(string s) => TimeSpan.ParseExact(s, @"h\:mm", CultureInfo.InvariantCulture);

    /// <summary>(after, until] にある実行予定（時刻順）。</summary>
    public IEnumerable<(DateTime When, PointKind Kind)> Occurrences(DateTime after, DateTime until)
    {
        if (!Enabled) yield break;
        for (var day = after.Date; day <= until.Date; day = day.AddDays(1))
        {
            var list = new List<(DateTime, PointKind)>();
            if (Incremental == "daily" && Days.Contains(day.DayOfWeek))
                foreach (var t in Times) list.Add((day + T(t), PointKind.Incremental));
            else if (Incremental == "hourly" && IntervalHours > 0)
                for (var t = T(HourlyStart); t < TimeSpan.FromDays(1); t += TimeSpan.FromHours(IntervalHours))
                    list.Add((day + t, PointKind.Incremental));
            bool fullDay = Full switch
            {
                "weekly" => day.DayOfWeek == FullDay,
                "monthly" => day.Day == Math.Min(FullDayOfMonth, DateTime.DaysInMonth(day.Year, day.Month)),
                _ => false,
            };
            if (fullDay) list.Add((day + T(FullTime), PointKind.Full));
            foreach (var g in list.GroupBy(x => x.Item1).OrderBy(g => g.Key))
            {
                if (g.Key <= after || g.Key > until) continue;
                yield return (g.Key, g.Any(x => x.Item2 == PointKind.Full) ? PointKind.Full : PointKind.Incremental);
            }
        }
    }

    public (DateTime When, PointKind Kind)? Next(DateTime after)
    {
        foreach (var o in Occurrences(after, after.AddDays(62))) return o;
        return null;
    }

    public string Describe()
    {
        if (!Enabled) return "スケジュールなし（手動のみ）";
        string[] dn = ["日", "月", "火", "水", "木", "金", "土"];
        var inc = Incremental switch
        {
            "daily" => (Days.Count == 7 ? "毎日" : "毎週 " + string.Concat(Days.OrderBy(d => ((int)d + 6) % 7).Select(d => dn[(int)d]))) + " " + string.Join(", ", Times),
            "hourly" => $"{HourlyStart} から {IntervalHours} 時間ごと",
            _ => "なし",
        };
        var full = Full switch
        {
            "weekly" => $"毎週{dn[(int)FullDay]}曜 {FullTime}",
            "monthly" => $"毎月 {FullDayOfMonth} 日 {FullTime}",
            _ => "なし（最初の 1 回だけ）",
        };
        return $"増分: {inc} ／ フル: {full}";
    }
}
