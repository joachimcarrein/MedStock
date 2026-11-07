using System.Net;
using System.Text.RegularExpressions;
using MedStock.Web.Domain;

namespace MedStock.Tests;

/// <summary>Detail actions (§3.4) and archive (§3.11) through HTTP, on 2026-11-17 (fixed clock).</summary>
public class DetailWebTests : IClassFixture<AddonWebFactory>
{
    private readonly AddonWebFactory _f;
    public DetailWebTests(AddonWebFactory f) => _f = f;

    private int NewMed(UsageMode mode = UsageMode.PerDay, decimal units = 160, decimal min = 0)
    {
        return _f.WithDb(db =>
        {
            var p = db.Profiles.FirstOrDefault() ?? db.Profiles.Add(new Profile { Name = "D" }).Entity;
            var m = new Medication
            {
                Profile = p, Name = "Nasalix", Type = MedicationType.NasalSpray, UnitsPerPackage = 140, UsageMode = mode,
                PerDay = mode == UsageMode.PerDay ? 8 : 0, MinStockUnits = min,
                Snapshots = [new Snapshot { Date = T.D("2026-11-17"), TotalUnits = units, Reason = SnapshotReason.Initial }],
            };
            db.Medications.Add(m);
            db.SaveChanges();
            return m.Id;
        });
    }

    private decimal Units(int id) => _f.WithDb(db => db.Snapshots.Where(s => s.MedicationId == id).OrderByDescending(s => s.Id).First().TotalUnits);
    private int LatestId(int id) => _f.WithDb(db => db.Snapshots.Where(s => s.MedicationId == id).Max(s => s.Id));

    private async Task<(HttpClient, string)> ClientAsync(int id)
    {
        var c = _f.Client(ingressPath: null);
        c.DefaultRequestHeaders.AcceptLanguage.ParseAdd("nl-BE");
        var html = await c.GetStringAsync($"/Medications/Details?id={id}");
        return (c, Regex.Match(html, "name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"").Groups[1].Value);
    }

    private static FormUrlEncodedContent Form(string token, params (string, string)[] fields) =>
        new([new("__RequestVerificationToken", token), .. fields.Select(f => new KeyValuePair<string, string>(f.Item1, f.Item2))]);

    [Fact]
    public async Task Purchase_adds_packages_and_confirms()
    {
        var id = NewMed();
        var (c, token) = await ClientAsync(id);
        var resp = await c.PostAsync($"/Medications/Details?id={id}&handler=Purchase", Form(token, ("packages", "2")));
        Assert.Equal(HttpStatusCode.Redirect, resp.StatusCode);
        Assert.Equal(160m + 280m, Units(id));
        var html = WebUtility.HtmlDecode(await c.GetStringAsync(resp.Headers.Location!.ToString()));
        Assert.Contains("Toegevoegd: +2 flesjes", html);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("")]
    public async Task Purchase_needs_at_least_one(string n)
    {
        var id = NewMed();
        var (c, token) = await ClientAsync(id);
        var resp = await c.PostAsync($"/Medications/Details?id={id}&handler=Purchase", Form(token, ("packages", n)));
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Contains("Minstens 1", await resp.Content.ReadAsStringAsync());
        Assert.Equal(160m, Units(id));
    }

    [Theory]
    [InlineData(false, 232)] // 100 + 1 × 140 − today's 8
    [InlineData(true, 240)]
    public async Task Recount_applies_the_taken_today_checkbox(bool taken, decimal expected)
    {
        var id = NewMed();
        var (c, token) = await ClientAsync(id);
        var fields = new List<(string, string)> { ("expectedSnapshotId", LatestId(id).ToString()), ("openUnits", "100"), ("sealedPackages", "1") };
        if (taken) fields.Add(("takenToday", "true"));
        var resp = await c.PostAsync($"/Medications/Details?id={id}&handler=Recount", Form(token, [.. fields]));
        Assert.Equal(HttpStatusCode.Redirect, resp.StatusCode);
        Assert.Equal(expected, Units(id));
    }

    [Fact]
    public async Task Stale_recount_returns_409()
    {
        var id = NewMed();
        var (c, token) = await ClientAsync(id);
        var seen = LatestId(id);
        await c.PostAsync($"/Medications/Details?id={id}&handler=Purchase", Form(token, ("packages", "1")));
        var resp = await c.PostAsync($"/Medications/Details?id={id}&handler=Recount",
            Form(token, ("expectedSnapshotId", seen.ToString()), ("openUnits", "5")));
        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
        Assert.Contains("Iemand anders heeft net iets gewijzigd", await resp.Content.ReadAsStringAsync());
        Assert.Equal(300m, Units(id));
    }

    [Fact]
    public async Task Pause_and_resume()
    {
        var id = NewMed();
        var (c, token) = await ClientAsync(id);
        await c.PostAsync($"/Medications/Details?id={id}&handler=Pause", Form(token, ("expectedSnapshotId", LatestId(id).ToString()), ("paused", "true")));
        Assert.True(_f.WithDb(db => db.Medications.Single(m => m.Id == id).Paused));
        Assert.Contains("gepauzeerd", await c.GetStringAsync($"/Medications/Details?id={id}"));
        await c.PostAsync($"/Medications/Details?id={id}&handler=Pause", Form(token, ("expectedSnapshotId", LatestId(id).ToString()), ("paused", "false")));
        Assert.False(_f.WithDb(db => db.Medications.Single(m => m.Id == id).Paused));
    }

    [Fact]
    public async Task As_needed_intake_and_buttons()
    {
        var id = NewMed(UsageMode.AsNeeded, units: 3, min: 10);
        var (c, token) = await ClientAsync(id);
        Assert.Contains("handler=Intake", await c.GetStringAsync($"/Medications/Details?id={id}"));
        await c.PostAsync($"/Medications/Details?id={id}&handler=Intake", Form(token, ("units", "5")));
        Assert.Equal(0m, Units(id));
    }

    [Fact]
    public async Task Archive_hides_from_overview_restore_and_delete_permanently()
    {
        var id = NewMed();
        var photo = Path.Combine(_f.DataDir, "photos", $"{id}.jpg");
        Directory.CreateDirectory(Path.GetDirectoryName(photo)!);
        await File.WriteAllBytesAsync(photo, [0xFF, 0xD8, 0xFF]);
        _f.WithDb(db =>
        {
            db.NotificationLogs.Add(new NotificationLog { MedicationId = id, Level = 14, SentAt = DateTime.UtcNow });
            return db.SaveChanges();
        });

        var (c, token) = await ClientAsync(id);
        var resp = await c.PostAsync($"/Medications/Details?id={id}&handler=Archive", Form(token));
        Assert.Equal(HttpStatusCode.Redirect, resp.StatusCode);
        Assert.EndsWith("/", resp.Headers.Location!.ToString());
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync($"/Medications/Details?id={id}")).StatusCode);
        Assert.Contains("Nasalix", await c.GetStringAsync("/Medications/Archived"));

        await c.PostAsync($"/Medications/Archived?id={id}&handler=Restore", Form(token));
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync($"/Medications/Details?id={id}")).StatusCode);

        // Only archived medication can be deleted permanently.
        await c.PostAsync($"/Medications/Archived?id={id}&handler=Delete", Form(token));
        Assert.True(_f.WithDb(db => db.Medications.Any(m => m.Id == id)));

        await c.PostAsync($"/Medications/Details?id={id}&handler=Archive", Form(token));
        await c.PostAsync($"/Medications/Archived?id={id}&handler=Delete", Form(token));
        Assert.False(_f.WithDb(db => db.Medications.Any(m => m.Id == id)));
        Assert.False(_f.WithDb(db => db.Snapshots.Any(s => s.MedicationId == id)));
        Assert.False(_f.WithDb(db => db.NotificationLogs.Any(n => n.MedicationId == id)));
        Assert.False(File.Exists(photo));
    }
}
