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
        t.TotalHours >= 1 ? $"{(int)t.TotalHours}時間{t.Minutes}分" :
        t.TotalMinutes >= 1 ? $"{t.Minutes}分{t.Seconds}秒" : $"{t.TotalSeconds:0.0}秒";

    public static string Rate(double bps) => Size((long)bps) + "/s";
}
