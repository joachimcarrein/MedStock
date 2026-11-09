using System.Globalization;
using System.Net;
using MedStock.Web.Domain;
using MedStock.Web.Services;

namespace MedStock.Tests;

/// <summary>Prescription screen (§3.2) on 2026-11-17 with the examples from the plan.</summary>
public class DoctorWebTests : IClassFixture<AddonWebFactory>
{
    private readonly AddonWebFactory _f;
    private readonly int _husband, _other;

    public DoctorWebTests(AddonWebFactory f)
    {
        _f = f;
        (_husband, _other) = _f.WithDb(db =>
        {
            if (db.Profiles.Any()) return (db.Profiles.Single(p => p.Name == "Husband").Id, db.Profiles.Single(p => p.Name == "Other").Id);
            var j = new Profile { Name = "Husband", SortOrder = 0 };
            var o = new Profile { Name = "Other", SortOrder = 1, CoverageDays = 30 };
            Add(j, "Nasalix", "neusspray", MedicationType.NasalSpray, 140, UsageMode.PerDay, 8, new decimal[7], "2026-11-07", 240);
            Add(j, "Weekject", "0,5 mg", MedicationType.Injection, 4, UsageMode.Weekly, 0, T.Saturday, "2026-11-09", 3);
            Add(j, "Vitamin D", null, MedicationType.Capsule, 90, UsageMode.PerDay, 1, new decimal[7], "2026-11-17", 400);
            Add(j, "Paused", null, MedicationType.Pill, 28, UsageMode.PerDay, 1, new decimal[7], "2026-11-17", 1, paused: true);
            Add(o, "Nasalix", null, MedicationType.NasalSpray, 140, UsageMode.PerDay, 8, new decimal[7], "2026-11-07", 240);
            Add(o, "Painkiller", null, MedicationType.Pill, 20, UsageMode.PerDay, 1, new decimal[7], "2026-11-17", 5, otc: true);
            db.SaveChanges();
            return (j.Id, o.Id);

            void Add(Profile p, string name, string? strength, MedicationType type, decimal upp, UsageMode mode, decimal perDay,
                decimal[] weekly, string date, decimal units, bool paused = false, bool otc = false) =>
                db.Medications.Add(new Medication
                {
                    Profile = p, Name = name, Strength = strength, Type = type, UnitsPerPackage = upp, UsageMode = mode, PerDay = perDay,
                    Weekly = weekly, Paused = paused, OverTheCounter = otc,
                    Snapshots = [new Snapshot { Date = T.D(date), TotalUnits = units, Reason = SnapshotReason.Initial }],
                });
        });
    }

    private async Task<string> GetAsync(string url)
    {
        var c = _f.Client("doc-" + url.GetHashCode(), ingressPath: null);
        c.DefaultRequestHeaders.AcceptLanguage.ParseAdd("nl-BE");
        return WebUtility.HtmlDecode(await c.GetStringAsync(url));
    }

    [Fact]
    public async Task Share_text_lists_what_to_ask_for()
    {
        var html = await GetAsync($"/Doctor?profile={_husband}");
        var nl = new CultureInfo("nl");
        var expected = string.Join("\n",
            $"Voorschrift Husband – {Texts.LongDate(new DateOnly(2026, 11, 17), nl)}",
            "- Weekject 0,5 mg: 3 verpakkingen (laatste dosis " + Texts.ShortDate(new DateOnly(2026, 11, 28), nl) + ")",
            "- Nasalix neusspray: 4 flesjes (nog 20 dagen)");
        Assert.Contains($">{expected}</textarea>", html);
        Assert.Contains("vraag 4 flesjes", html);
        Assert.Contains("Niets nodig (2)", html); // Vitamin D (400 days) and the paused one
        Assert.Contains("(gepauzeerd)", html);
    }

    [Fact]
    public async Task Coverage_days_cascade_from_the_profile()
    {
        // Other: 30 days × 8 = 240 needed − 160 remaining = 80 → 1 bottle.
        var html = await GetAsync($"/Doctor?profile={_other}");
        Assert.Contains("vraag 1 flesje", html);
    }

    [Fact]
    public async Task All_profiles_is_the_default_without_a_remembered_filter_with_one_block_per_profile()
    {
        foreach (var url in new[] { "/Doctor", "/Doctor?profile=all" })
        {
            var html = await GetAsync(url);
            var date = Texts.LongDate(new DateOnly(2026, 11, 17), new CultureInfo("nl"));
            var text = html.Split("<textarea")[1].Split("</textarea>")[0];
            Assert.Contains($"Voorschrift Husband – {date}\n- Weekject", text);
            Assert.Contains($"(nog 20 dagen)\n\nVoorschrift Other – {date}\n- Nasalix: 1 flesje", text); // blank line between profiles
            Assert.Contains("aria-current=\"page\">Alles</a>", html);
        }
    }

    [Fact]
    public async Task Over_the_counter_is_not_asked_for_but_listed_to_buy()
    {
        var html = await GetAsync($"/Doctor?profile={_other}");
        var text = html.Split("<textarea")[1].Split("</textarea>")[0];
        Assert.DoesNotContain("Painkiller", text);
        Assert.Contains("Vrij verkrijgbaar, zelf kopen (1)", html);
        Assert.Contains("Painkiller", html);
        Assert.Contains("koop 2 dozen", html); // 30 days × 1 − 5 = 25 → 2 boxes of 20
    }

    [Fact]
    public void Vacation_need_counts_the_trip_and_what_to_buy_before_leaving()
    {
        var today = T.D("2026-11-17");
        var nasalix = new StockInput(UsageMode.PerDay, 8, new decimal[7], false, 0, 140, T.D("2026-11-07"), 240);
        var n = PrescriptionCalculator.Vacation(nasalix, today, T.D("2026-12-01"), 14, 140)!;
        Assert.Equal(112m, n.NeedUnits);       // 14 × 8
        Assert.Equal(2, n.TakePortions);       // the opened bottle (56) plus a new one
        Assert.Equal(56m, n.UnitsAtDeparture); // 240 − 23 × 8 on 30 Nov
        Assert.Equal(1, n.BuyPackages);        // 27 × 8 = 216 until 14 Dec, 160 in stock → 56 short

        var weekject = new StockInput(UsageMode.Weekly, 0, T.Saturday, false, 0, 4, T.D("2026-11-09"), 3);
        var w = PrescriptionCalculator.Vacation(weekject, today, T.D("2026-12-01"), 14)!;
        Assert.Equal(2m, w.NeedUnits);         // Sat 5 and 12 Dec
        Assert.Equal(0m, w.UnitsAtDeparture);  // 21 and 28 Nov use the last two
        Assert.Equal(1, w.BuyPackages);
        Assert.Equal(2, w.TakePortions);       // per injection

        Assert.Null(PrescriptionCalculator.Vacation(nasalix with { Paused = true }, today, T.D("2026-12-01"), 14));
        Assert.Null(PrescriptionCalculator.Vacation(nasalix with { Mode = UsageMode.AsNeeded }, today, T.D("2026-12-01"), 14));
    }

    [Theory]
    [InlineData(7, 0, 10, 1)]     // a week of pills, strips of 10, no opened strip
    [InlineData(7, 23, 10, 2)]    // opened strip with 3 left, plus one full strip
    [InlineData(5, 25, 10, 1)]    // the opened strip (5) is enough
    [InlineData(56, 240, 140, 1)] // 100 left in the opened bottle covers 56
    [InlineData(112, 56, 140, 2)]
    [InlineData(7, 30, 1, 7)]     // per unit
    [InlineData(0, 30, 10, 0)]
    public void Portions_to_take_along_start_with_the_opened_one(decimal need, decimal atDeparture, decimal portion, int expected)
    {
        Assert.Equal(expected, PrescriptionCalculator.Portions(need, atDeparture, portion));
    }

    [Fact]
    public void Take_portion_defaults_to_a_whole_bottle_for_sprays_and_a_unit_for_pills()
    {
        Assert.Equal(140m, PrescriptionCalculator.TakePortion(new Medication { Type = MedicationType.NasalSpray, UnitsPerPackage = 140 }));
        Assert.Equal(1m, PrescriptionCalculator.TakePortion(new Medication { Type = MedicationType.Pill, UnitsPerPackage = 30 }));
        Assert.Equal(10m, PrescriptionCalculator.TakePortion(new Medication { Type = MedicationType.Pill, UnitsPerPackage = 30, TakeAlongUnits = 10 }));
    }

    [Fact]
    public async Task Vacation_page_lists_what_to_buy_and_take()
    {
        var html = await GetAsync($"/Vacation?from=2026-12-01&days=14&profile={_husband}");
        var nl = new CultureInfo("nl");
        var dec1 = Texts.ShortDate(new DateOnly(2026, 12, 1), nl);
        Assert.Contains($"koop 1 flesje vóór {dec1}", html);
        Assert.Contains($"koop 1 verpakking vóór {dec1}", html);
        Assert.Contains("genoeg in voorraad", html); // Vitamin D
        Assert.Contains("neem 2 flesjes mee (112 doses nodig)", html);
        Assert.Contains("nog 56 doses bij vertrek", html);
        Assert.Contains("zelf nakijken: Paused", html);
        Assert.Contains($"Reis vanaf {Texts.LongDate(new DateOnly(2026, 12, 1), nl)}, 14 dagen", html);
        Assert.DoesNotContain("Other", html.Split("<textarea")[1]); // only the chosen profile

        Assert.Contains("Op reis?", await GetAsync("/Doctor"));
        var c = _f.Client("vac-bad", ingressPath: null);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.GetAsync("/Vacation?from=2026-11-01&days=7")).StatusCode);
    }
}
