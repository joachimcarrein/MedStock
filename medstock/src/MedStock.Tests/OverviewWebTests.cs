using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using MedStock.Web.Domain;
using MedStock.Web.Services;

namespace MedStock.Tests;

/// <summary>Overview (§3.3) on 2026-11-17 (fixed clock): sorting, badges, dates and the remembered profile filter.</summary>
public class OverviewWebTests : IClassFixture<AddonWebFactory>
{
    private readonly AddonWebFactory _f;
    private readonly int _husband, _wife;

    public OverviewWebTests(AddonWebFactory f)
    {
        _f = f;
        (_husband, _wife) = _f.WithDb(db =>
        {
            if (db.Profiles.Any()) return (db.Profiles.Single(p => p.Name == "J").Id, db.Profiles.Single(p => p.Name == "M").Id);
            var j = new Profile { Name = "J", SortOrder = 0 };
            var m = new Profile { Name = "M", SortOrder = 1 };
            db.Profiles.AddRange(j, m);
            Add(j, "Nasalix", MedicationType.NasalSpray, 140, UsageMode.PerDay, 8, new decimal[7], "2026-11-07", 240);
            Add(j, "Weekject", MedicationType.Injection, 4, UsageMode.Weekly, 0, T.Saturday, "2026-11-09", 3);
            Add(m, "Paracetamol", MedicationType.Pill, 20, UsageMode.AsNeeded, 0, new decimal[7], "2026-11-01", 6, min: 10);
            db.SaveChanges();
            return (j.Id, m.Id);

            void Add(Profile p, string name, MedicationType type, decimal upp, UsageMode mode, decimal perDay, decimal[] weekly,
                string date, decimal units, decimal min = 0) =>
                db.Medications.Add(new Medication
                {
                    Profile = p, Name = name, Type = type, UnitsPerPackage = upp, UsageMode = mode, PerDay = perDay, Weekly = weekly,
                    MinStockUnits = min,
                    Snapshots = [new Snapshot { Date = T.D(date), TotalUnits = units, Reason = SnapshotReason.Initial }],
                });
        });
    }

    private HttpClient Nl(string user)
    {
        var c = _f.Client(user, ingressPath: null);
        c.DefaultRequestHeaders.AcceptLanguage.ParseAdd("nl-BE");
        return c;
    }

    private static List<string> Names(string html) =>
        Regex.Matches(html, "<span class=\"name\">([^<]+?)\\s*</span>").Select(m => m.Groups[1].Value.Trim()).ToList();

    [Fact]
    public async Task Cards_are_sorted_by_urgency_with_badges_and_dates()
    {
        var html = WebUtility.HtmlDecode(await Nl("ov-1").GetStringAsync("/")); // Razor encodes "+" and "·"
        var nl = new CultureInfo("nl");
        // Paracetamol: as needed under the minimum (rank 2, no days); Weekject: 11 days, critical (rank 2); Nasalix: 20 days, warning.
        Assert.Equal(["Weekject", "Paracetamol", "Nasalix"], Names(html));
        Assert.Contains("nog 20 dagen", html);
        Assert.Contains("1 flesje + 20 doses", html);
        Assert.Contains("laatste dosis " + Texts.ShortDate(new DateOnly(2026, 12, 7), nl), html);
        Assert.Contains("nog 11 dagen", html);
        Assert.Contains("laatste dosis " + Texts.ShortDate(new DateOnly(2026, 11, 28), nl), html);
        Assert.Contains("zo nodig · nog 6 pillen", html);
        Assert.Contains("state-orange", html);
        Assert.Contains("state-red", html);
    }

    [Fact]
    public async Task French_browser_gets_the_french_ui_and_language_choice()
    {
        var c = _f.Client("fr-user", ingressPath: null);
        c.DefaultRequestHeaders.AcceptLanguage.ParseAdd("fr-BE");
        var html = System.Net.WebUtility.HtmlDecode(await c.GetStringAsync("/"));
        Assert.Contains("Aperçu", html);
        Assert.Contains("<html lang=\"fr\"", html);
        var settings = System.Net.WebUtility.HtmlDecode(await c.GetStringAsync("/Settings"));
        Assert.Contains(">Français</option>", settings);
    }

    [Theory]
    [InlineData("nl-BE", "Voorschrift")] // Belgian variant (SharedResource.nl-BE.resx)
    [InlineData("nl-NL", "Recept")]      // no nl-NL file: falls back to nl
    [InlineData("nl", "Recept")]
    public async Task Regional_variant_only_overrides_its_own_words(string lang, string tab)
    {
        var c = _f.Client("region-" + lang, ingressPath: null);
        c.DefaultRequestHeaders.AcceptLanguage.ParseAdd(lang);
        var html = System.Net.WebUtility.HtmlDecode(await c.GetStringAsync("/"));
        Assert.Contains(tab, html);
        Assert.DoesNotContain(tab == "Recept" ? "Voorschrift" : "Recept", html);
        Assert.Contains("Overzicht", html); // everything else from nl
        var settings = System.Net.WebUtility.HtmlDecode(await c.GetStringAsync("/Settings"));
        Assert.Contains(">Nederlands (België)</option>", settings);
    }

    [Fact]
    public async Task Profile_filter_is_remembered_per_user()
    {
        var c = Nl("ov-2");
        var html = await c.GetStringAsync("/");
        // Each chip is its own form with a hidden field (iOS does not send a tapped button's value reliably).
        Assert.Contains($"<input type=\"hidden\" name=\"profileId\" value=\"{_wife}\" />", html);
        Assert.DoesNotContain("name=\"profileId\" value=\"" + _wife + "\" class", html);
        var token = Regex.Match(html, "name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"").Groups[1].Value;
        var resp = await c.PostAsync("/?handler=Filter", new FormUrlEncodedContent(
            [new("__RequestVerificationToken", token), new("profileId", _wife.ToString())]));
        Assert.Equal(HttpStatusCode.Redirect, resp.StatusCode);
        Assert.EndsWith("/", resp.Headers.Location!.ToString()); // ingress root needs the trailing slash

        var filtered = await c.GetStringAsync("/");
        Assert.Equal(["Paracetamol"], Names(filtered));
        Assert.Equal(3, Names(await Nl("ov-3").GetStringAsync("/")).Count); // another user still sees everything

        // "+" preselects the filtered profile in the new-medication form.
        var fab = Regex.Match(filtered, "<a class=\"fab\" href=\"([^\"]+)\"").Groups[1].Value;
        Assert.EndsWith($"?profileId={_wife}", WebUtility.HtmlDecode(fab));
        var form = await c.GetStringAsync(WebUtility.HtmlDecode(fab));
        Assert.Matches($"<option value=\"{_wife}\" selected=\"selected\">", form);

        await c.PostAsync("/?handler=Filter", new FormUrlEncodedContent(
            [new("__RequestVerificationToken", token), new("profileId", "")]));
        var all = await c.GetStringAsync("/");
        Assert.Equal(3, Names(all).Count);
        Assert.DoesNotContain("profileId=", Regex.Match(all, "<a class=\"fab\" href=\"([^\"]+)\"").Groups[1].Value);
    }

    [Fact]
    public async Task Filter_on_a_profile_without_medication_shows_the_empty_hint()
    {
        var empty = _f.WithDb(db =>
        {
            var p = db.Profiles.FirstOrDefault(x => x.Name == "Empty") ?? db.Profiles.Add(new Profile { Name = "Empty", SortOrder = 5 }).Entity;
            db.SaveChanges();
            return p.Id;
        });
        var c = Nl("ov-4");
        await c.GetStringAsync("/"); // creates the user settings row
        _f.WithDb(db =>
        {
            db.UserSettings.Single(s => s.UserId == "ov-4").LastProfileFilter = empty;
            return db.SaveChanges();
        });
        var html = await c.GetStringAsync("/");
        Assert.Contains("Nog geen medicatie", html);
        Assert.Empty(Names(html));
        Assert.True(_husband > 0);
    }
}
