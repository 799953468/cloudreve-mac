using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Avalonia.Markup.Xaml;

namespace Cloudreve.Desktop;

internal enum UiLanguage
{
    Chinese,
    English,
    SystemDefault
}

// Every user-visible string is looked up by a dotted key (for example "activity.upload") in Assets/Locales/{zh,en}.json.
internal static partial class UiText
{
    private static readonly Lazy<JsonElement> Chinese = new(() => Load("zh"));
    private static readonly Lazy<JsonElement> English = new(() => Load("en"));
    internal static UiLanguage Language { get; set; }

    internal static UiLanguage Resolve(UiLanguage language)
    {
        if (language != UiLanguage.SystemDefault)
        {
            return language;
        }

        return CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "zh" ? UiLanguage.Chinese : UiLanguage.English;
    }

    internal static JsonElement Load(string locale)
    {
        using (var stream = typeof(UiText).Assembly.GetManifestResourceStream($"Cloudreve.Desktop.Locales.{locale}.json")
                            ?? throw new InvalidOperationException("Missing UI translation: " + locale))
        {
            return JsonSerializer.Deserialize(stream, DesktopJson.Default.JsonElement);
        }
    }

    // Falls back to Chinese, then to the key itself, so a missing entry never hides the error message it was meant to show.
    internal static string Get(string key)
    {
        if (Language == UiLanguage.English && TryFind(English.Value, key, out string? english))
        {
            return english;
        }

        return TryFind(Chinese.Value, key, out string? chinese) ? chinese : key;
    }

    // Fills {{name}} placeholders. Values are inserted as-is: file names and server messages are never translated.
    internal static string Get(string key, params (string Name, object? Value)[] values)
    {
        return Placeholder().Replace(Get(key), match =>
        {
            foreach (var (name, value) in values)
            {
                if (name == match.Groups[1].Value)
                {
                    return Convert.ToString(value, CultureInfo.CurrentCulture) ?? "";
                }
            }

            return match.Value;
        });
    }

    internal static bool TryFind(JsonElement root, string key, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? text)
    {
        var value = root;
        foreach (string part in key.Split('.'))
        {
            if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(part, out value))
            {
                text = null;
                return false;
            }
        }

        text = value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        return text != null;
    }

    [GeneratedRegex(@"\{\{(\w+)\}\}")]
    private static partial Regex Placeholder();
}

// {d:Tr settings.title} in XAML.
internal sealed class TrExtension(string key) : MarkupExtension
{
    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        return UiText.Get(key);
    }
}
