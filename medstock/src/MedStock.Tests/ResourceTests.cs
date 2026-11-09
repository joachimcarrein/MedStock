using System.Text.RegularExpressions;
using System.Xml.Linq;
using MedStock.Web.Middleware;

namespace MedStock.Tests;

/// <summary>
/// The resx files are the only source of UI texts. Every language must have exactly the keys of the English file, with the
/// same placeholders, and every language file must be offered in the UI (and the other way round).
/// </summary>
public partial class ResourceTests
{
    private static readonly string Dir = FindResources();

    private static string FindResources()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
        {
            var p = Path.Combine(d.FullName, "MedStock.Web", "Resources");
            if (File.Exists(Path.Combine(p, "SharedResource.resx"))) return p;
        }
        throw new DirectoryNotFoundException("MedStock.Web/Resources not found");
    }

    private static Dictionary<string, string> Load(string file) =>
        XDocument.Load(file).Root!.Elements("data")
            .ToDictionary(e => (string)e.Attribute("name")!, e => (string?)e.Element("value") ?? "");

    private static List<string> LanguageCodes() =>
        [.. Directory.GetFiles(Dir, "SharedResource.*.resx").Select(f => Path.GetFileName(f).Split('.')[1])];

    public static TheoryData<string> Languages() => [.. LanguageCodes()];

    [Theory]
    [MemberData(nameof(Languages))]
    public void Every_language_has_the_english_keys_and_placeholders(string lang)
    {
        var en = Load(Path.Combine(Dir, "SharedResource.resx"));
        var other = Load(Path.Combine(Dir, $"SharedResource.{lang}.resx"));
        Assert.Empty(en.Keys.Except(other.Keys));   // missing translations
        Assert.Empty(other.Keys.Except(en.Keys));   // leftovers
        Assert.Empty(en.Where(kv => !Placeholders(kv.Value).SetEquals(Placeholders(other[kv.Key]))).Select(kv => kv.Key));
        Assert.Empty(other.Where(kv => string.IsNullOrWhiteSpace(kv.Value)).Select(kv => kv.Key));
    }

    [Fact]
    public void Language_files_and_ui_cultures_match()
    {
        var files = LanguageCodes().Append("en").OrderBy(x => x);
        Assert.Equal(files, LocalizationSetup.UiCultures.Select(c => c.Name).OrderBy(x => x));
    }

    private static HashSet<string> Placeholders(string s) => [.. PlaceholderRegex().Matches(s).Select(m => m.Groups[1].Value)];

    [GeneratedRegex(@"\{(\d+)[^}]*\}")]
    private static partial Regex PlaceholderRegex();
}
