using System.Windows.Data;
using System.Windows.Markup;

namespace ClipAssist.Localization;

/// <summary>
/// XAML helper: <c>Text="{loc:Loc Main.Title}"</c>.
/// It returns a binding to the localization service indexer, so every visual
/// using it refreshes in place when the language changes.
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
            Source = LocalizationService.Instance,
            Mode = BindingMode.OneWay,
        };
        return binding.ProvideValue(serviceProvider);
    }
}