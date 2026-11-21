using System.Globalization;
using System.Text.Json;
using Avalonia.Markup.Xaml;

namespace Cloudreve.Desktop;

internal enum UiLanguage
{
    Chinese,
    English
}

internal static class UiText
{
    private static readonly Lazy<Dictionary<string, string>> English = new(ReadEnglish);
    internal static UiLanguage Language { get; set; }

    private static Dictionary<string, string> ReadEnglish()
    {
        using (var stream = typeof(UiText).Assembly.GetManifestResourceStream("Cloudreve.Desktop.English.json")
                            ?? throw new InvalidOperationException("Missing English UI resource."))
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(stream) ?? throw new InvalidDataException("Invalid English UI resource.");
        }
    }

    internal static string Get(string text)
    {
        return Translate(text, Language);
    }

    internal static string Translate(string text, UiLanguage language)
    {
        if (language == UiLanguage.English && English.Value.TryGetValue(text, out string? translated))
        {
            return translated;
        }

        return text;
    }

    internal static string Format(FormattableString template)
    {
        return string.Format(CultureInfo.CurrentCulture, Get(template.Format), template.GetArguments());
    }
}

internal sealed class TrExtension(string text) : MarkupExtension
{
    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        return UiText.Get(text);
    }
}