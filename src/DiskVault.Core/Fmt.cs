namespace DiskVault;

public static class Fmt
{
    public static string Size(long b)
    {
        string[] u = ["B", "KB", "MB", "GB", "TB"];
        double v = b;
        int i = 0;
        while (Math.Abs(v) >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
        return i == 0 ? $"{b} B" : $"{v:0.##} {u[i]}";
    }

    public static string Duration(TimeSpan t) =>
        t.TotalHours >= 1 ? T("{0}時間{1}分", (int)t.TotalHours, t.Minutes) :
        t.TotalMinutes >= 1 ? T("{0}分{1}秒", t.Minutes, t.Seconds) : T("{0:0.0}秒", t.TotalSeconds);

    public static string Rate(double bps) => Size((long)bps) + "/s";
}
