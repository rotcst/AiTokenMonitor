using System.ComponentModel;
using System.Windows.Markup;
using CodexWeeklyMonitor.Services;
using Binding = System.Windows.Data.Binding;
using BindingMode = System.Windows.Data.BindingMode;

namespace CodexWeeklyMonitor.Localization;

/// <summary>
/// Indexer surface that XAML bindings read through, so a language change refreshes every bound
/// string at once by raising a single indexer <see cref="PropertyChanged"/>.
/// </summary>
public sealed class TranslationSource : INotifyPropertyChanged
{
    public static TranslationSource Instance { get; } = new();

    private TranslationSource()
    {
        Loc.Changed += (_, _) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
    }

    public string this[string key] => Loc.T(key);

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>
/// XAML markup extension: <c>Text="{loc:Str card.weekly}"</c>. Binds to
/// <see cref="TranslationSource"/> so the text updates live when the language changes.
/// </summary>
[MarkupExtensionReturnType(typeof(string))]
public sealed class StrExtension : MarkupExtension
{
    public StrExtension()
    {
    }

    public StrExtension(string key)
    {
        Key = key;
    }

    [ConstructorArgument("key")]
    public string Key { get; set; } = string.Empty;

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        var binding = new Binding($"[{Key}]")
        {
            Source = TranslationSource.Instance,
            Mode = BindingMode.OneWay,
        };
        return binding.ProvideValue(serviceProvider);
    }
}
