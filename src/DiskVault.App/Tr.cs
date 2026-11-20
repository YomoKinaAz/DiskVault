using System.Windows.Markup;

namespace DiskVault.App;

/// <summary>
/// XAML で表示言語に合わせた文字列を出す: Text="{local:Tr 'バックアップ ジョブ'}"。
/// 中身は日本語のまま書き、英語は Lang\en.json から引く（Loc.T）。言語の切り替えは再起動で反映。
/// </summary>
[MarkupExtensionReturnType(typeof(string))]
public sealed class TrExtension : MarkupExtension
{
    public TrExtension() { }
    public TrExtension(string text) => Text = text;

    [ConstructorArgument("text")]
    public string Text { get; set; } = "";

    public override object ProvideValue(IServiceProvider serviceProvider) => Loc.T(Text);
}
