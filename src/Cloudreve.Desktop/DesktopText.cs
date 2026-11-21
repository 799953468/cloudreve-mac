using System.Text.Json;
using Avalonia.Markup.Xaml;

namespace Cloudreve.Desktop;

internal sealed class DesktopText(string key) : MarkupExtension
{
    private static readonly Lazy<JsonElement> Chinese = new(() => Load("zh"));
    private static readonly Lazy<JsonElement> English = new(() => Load("en"));

    private static JsonElement Load(string locale)
    {
        using (var stream = typeof(DesktopText).Assembly.GetManifestResourceStream($"Cloudreve.Desktop.Windows.{locale}.json")
                            ?? throw new InvalidOperationException("Missing desktop translation: " + locale))
        {
            return JsonSerializer.Deserialize<JsonElement>(stream);
        }
    }

    internal static string Get(string key)
    {
        var value = UiText.Language == UiLanguage.English ? English.Value : Chinese.Value;
        foreach (string part in key.Split('.'))
        {
            value = value.GetProperty(part);
        }

        return value.GetString()!;
    }

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        return Get(key);
    }
}