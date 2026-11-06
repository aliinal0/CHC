using System.Windows.Data;
using System.Windows.Markup;
using CHC.Core.Localization;

namespace CHC.App.Localization;

/// <summary>
/// XAML: <c>Text="{loc:Tr nav.dashboard}"</c>. Binds to the string table so the text
/// changes immediately when the user switches language.
/// </summary>
[MarkupExtensionReturnType(typeof(object))]
public sealed class Tr : MarkupExtension
{
    public Tr() { }

    public Tr(string key) => Key = key;

    [ConstructorArgument("key")]
    public string Key { get; set; } = "";

    public override object ProvideValue(IServiceProvider serviceProvider) =>
        new Binding($"[{Key}]") { Source = Loc.Instance, Mode = BindingMode.OneWay }.ProvideValue(serviceProvider);
}
