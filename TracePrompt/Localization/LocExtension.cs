using System.Windows.Data;
using System.Windows.Markup;

namespace TracePrompt.Localization;

/// <summary>
/// XAML から <c>{loc:Loc Key}</c> の形で使う多言語文字列の拡張です。
/// 実体は <see cref="LocalizationManager.Instance"/> のインデクサーへの Binding なので、
/// <see cref="LocalizationManager.SetLanguage"/> が呼ばれるとバインド先の文字列がその場で更新されます。
/// </summary>
[MarkupExtensionReturnType(typeof(string))]
public sealed class LocExtension : MarkupExtension
{
    public LocExtension()
    {
    }

    public LocExtension(string key)
    {
        Key = key;
    }

    [ConstructorArgument("key")]
    public string Key { get; set; } = string.Empty;

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        var binding = new Binding($"[{Key}]")
        {
            Source = LocalizationManager.Instance,
            Mode = BindingMode.OneWay
        };
        return binding.ProvideValue(serviceProvider);
    }
}
