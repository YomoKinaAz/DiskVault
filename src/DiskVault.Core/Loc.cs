using System.Globalization;
using System.Reflection;
using System.Text.Json;

namespace DiskVault;

/// <summary>
/// 表示言語（日本語／英語）。日本語の文字列そのものをキーにして、英語の翻訳表（各アセンブリの埋め込みリソース
/// Lang\en.json：{"日本語": "English"}）を引く。表にない文字列は日本語のまま出す。
///   T("復元しました")                 … 固定の文字列
///   T("ディスク {0} を復元しました", n) … 書式付き（string.Format。キーは {0} のままの日本語）
/// 言語は AppConfig.Language（auto / ja / en）。auto は Windows の表示言語が日本語なら日本語、それ以外は英語。
/// 環境変数 DISKVAULT_LANG（ja / en）で上書きできる（テスト・WinPE 用）。
/// </summary>
public static class Loc
{
    static Dictionary<string, string>? _en;
    static readonly object Lock = new();

    public static string Lang { get; private set; } = Resolve(null);
    public static bool IsEnglish => Lang == "en";

    /// <summary>言語を決める（"auto" / "ja" / "en" / null）。プロセスの最初と設定の読み込み後に呼ぶ。</summary>
    public static void Set(string? setting)
    {
        Lang = Resolve(setting);
        var c = CultureInfo.GetCultureInfo(IsEnglish ? "en-US" : "ja-JP");
        CultureInfo.DefaultThreadCurrentUICulture = c;
        CultureInfo.CurrentUICulture = c;
        // 日付の曜日名などを表示言語に合わせる（数値の書式は en-US / ja-JP とも同じ）
        CultureInfo.DefaultThreadCurrentCulture = c;
        CultureInfo.CurrentCulture = c;
    }

    static string Resolve(string? setting)
    {
        var env = Environment.GetEnvironmentVariable("DISKVAULT_LANG");
        if (env is "ja" or "en") return env;
        if (setting is "ja" or "en") return setting;
        return CultureInfo.InstalledUICulture.TwoLetterISOLanguageName == "ja" || CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ja" ? "ja" : "en";
    }

    public static string T(string ja)
    {
        if (!IsEnglish || string.IsNullOrEmpty(ja)) return ja;
        // 複数行の文字列はソースの改行コード（git の CRLF 変換）に左右されないよう LF にそろえて引く
        var key = ja.Contains('\r') ? ja.Replace("\r\n", "\n") : ja;
        return Table().TryGetValue(key, out var en) ? en : ja;
    }

    public static string T(string ja, params object?[] args) => string.Format(CultureInfo.CurrentCulture, T(ja), args);

    /// <summary>翻訳表にない文字列（翻訳漏れの確認用）。</summary>
    public static bool Has(string ja) => Table().ContainsKey(ja.Replace("\r\n", "\n"));

    static Dictionary<string, string> Table()
    {
        if (_en != null) return _en;
        lock (Lock)
        {
            if (_en != null) return _en;
            var d = new Dictionary<string, string>(StringComparer.Ordinal);
            // 同じ日本語でも場所で訳が違うことがある（「保存」= GUI のボタン Save / CLI の列 Stored）。
            // エンジン（Core）→ そのほか → 実行中の exe の順に読み、exe の訳を優先する。
            var core = typeof(Loc).Assembly;
            var entry = Assembly.GetEntryAssembly();
            var asms = new[] { core }
                .Concat(AppDomain.CurrentDomain.GetAssemblies().Where(a => a != core && a != entry))
                .Append(entry).Distinct();
            foreach (var a in asms)
            {
                if (a == null || a.IsDynamic) continue;
                string[] names;
                try { names = a.GetManifestResourceNames(); } catch (NotSupportedException) { continue; }
                foreach (var n in names.Where(n => n.EndsWith("Lang.en.json", StringComparison.OrdinalIgnoreCase)))
                {
                    using var s = a.GetManifestResourceStream(n);
                    if (s == null) continue;
                    var map = JsonSerializer.Deserialize<Dictionary<string, string>>(s);
                    if (map != null) foreach (var (k, v) in map) d[k.Replace("\r\n", "\n")] = v;
                }
            }
            return _en = d;
        }
    }
}
