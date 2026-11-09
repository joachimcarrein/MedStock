using System.Net;
using System.Text.RegularExpressions;
using MedStock.Web.Data;
using MedStock.Web.Domain;
using MedStock.Web.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MedStock.Tests;

public class MedicationServiceTests : IClassFixture<AddonWebFactory>
{
    private readonly AddonWebFactory _f;
    public MedicationServiceTests(AddonWebFactory f) => _f = f;

    private async Task<T> Svc<T>(Func<MedicationService, Task<T>> f)
    {
        using var scope = _f.Services.CreateScope();
        return await f(scope.ServiceProvider.GetRequiredService<MedicationService>());
    }

    private async Task<int> NewMedAsync(decimal perDay = 8, decimal upp = 140, decimal open = 100, int sealedPackages = 1, bool taken = true,
        UsageMode mode = UsageMode.PerDay, decimal[]? weekly = null)
    {
        var profileId = _f.WithDb(db =>
        {
            var p = db.Profiles.FirstOrDefault() ?? db.Profiles.Add(new Profile { Name = "P" }).Entity;
            db.SaveChanges();
            return p.Id;
        });
        return await Svc(s => s.CreateAsync(new Medication
        {
            ProfileId = profileId, Name = "Med", UnitsPerPackage = upp, UsageMode = mode, PerDay = perDay, Weekly = weekly ?? new decimal[7],
        }, open, sealedPackages, taken));
    }

    private Snapshot Latest(int id) => _f.WithDb(db => db.Snapshots.Where(s => s.MedicationId == id).OrderByDescending(s => s.Id).First());

    [Fact]
    public async Task Initial_snapshot_uses_the_taken_today_checkbox()
    {
        var taken = await NewMedAsync(taken: true);
        Assert.Equal(240m, Latest(taken).TotalUnits);
        Assert.Equal(SnapshotReason.Initial, Latest(taken).Reason);
        Assert.Equal(new DateOnly(2026, 11, 17), Latest(taken).Date);
        var notTaken = await NewMedAsync(taken: false);
        Assert.Equal(232m, Latest(notTaken).TotalUnits);
    }

    [Fact]
    public async Task Two_concurrent_purchases_add_exactly_two_packages()
    {
        var id = await NewMedAsync();
        await Task.WhenAll(
            Svc(async s => { await s.PurchaseAsync(id, 1); return 0; }),
            Svc(async s => { await s.PurchaseAsync(id, 1); return 0; }));
        Assert.Equal(240m + 2 * 140m, Latest(id).TotalUnits);
        Assert.Equal(3, _f.WithDb(db => db.Snapshots.Count(s => s.MedicationId == id)));
    }

    [Fact]
    public async Task Stale_recount_is_rejected()
    {
        var id = await NewMedAsync();
        var seen = Latest(id).Id;
        await Svc(async s => { await s.PurchaseAsync(id, 1); return 0; });
        await Assert.ThrowsAsync<StaleSnapshotException>(() => Svc(async s => { await s.RecountAsync(id, seen, 50, 0, true); return 0; }));
        await Svc(async s => { await s.RecountAsync(id, Latest(id).Id, 50, 0, true); return 0; });
        Assert.Equal(50m, Latest(id).TotalUnits);
        Assert.Equal(SnapshotReason.Recount, Latest(id).Reason);
    }

    [Fact]
    public async Task Edit_with_usage_change_writes_dose_change_snapshot_with_old_usage()
    {
        var id = await NewMedAsync();
        // Pretend the initial count was 10 days ago: 240 − 10 × 8 = 160 remaining today.
        _f.WithDb(db =>
        {
            db.Snapshots.Single(s => s.MedicationId == id).Date = new DateOnly(2026, 11, 7);
            return db.SaveChanges();
        });
        await Svc(async s => { await s.UpdateAsync(id, Latest(id).Id, m => m.PerDay = 4); return 0; });
        var latest = Latest(id);
        Assert.Equal(SnapshotReason.DoseChange, latest.Reason);
        Assert.Equal(160m, latest.TotalUnits);
        Assert.Equal(4m, _f.WithDb(db => db.Medications.Single(m => m.Id == id).PerDay));
    }

    [Fact]
    public async Task Edit_without_usage_change_writes_no_snapshot_and_ignores_staleness()
    {
        var id = await NewMedAsync();
        await Svc(async s => { await s.UpdateAsync(id, expectedSnapshotId: -1, m => m.Name = "Renamed"); return 0; });
        Assert.Equal(1, _f.WithDb(db => db.Snapshots.Count(s => s.MedicationId == id)));
        Assert.Equal("Renamed", _f.WithDb(db => db.Medications.Single(m => m.Id == id).Name));
    }

    [Fact]
    public async Task Weekly_change_is_detected()
    {
        var id = await NewMedAsync(perDay: 0, upp: 4, open: 3, sealedPackages: 0, mode: UsageMode.Weekly, weekly: [0, 0, 0, 0, 0, 1, 0]);
        await Svc(async s => { await s.UpdateAsync(id, Latest(id).Id, m => m.Weekly = [0, 0, 0, 0, 0, 0, 1]); return 0; });
        Assert.Equal(SnapshotReason.DoseChange, Latest(id).Reason);
    }

    [Fact]
    public async Task Usage_change_with_stale_snapshot_is_rejected()
    {
        var id = await NewMedAsync();
        var seen = Latest(id).Id;
        await Svc(async s => { await s.IntakeAsync(id, 1); return 0; });
        await Assert.ThrowsAsync<StaleSnapshotException>(() => Svc(async s => { await s.UpdateAsync(id, seen, m => m.PerDay = 2); return 0; }));
        Assert.Equal(8m, _f.WithDb(db => db.Medications.Single(m => m.Id == id).PerDay));
    }

    [Fact]
    public async Task Pause_and_resume_snapshot_the_remaining_stock()
    {
        var id = await NewMedAsync();
        await Svc(async s => { await s.SetPausedAsync(id, Latest(id).Id, true); return 0; });
        Assert.Equal(SnapshotReason.Pause, Latest(id).Reason);
        Assert.True(_f.WithDb(db => db.Medications.Single(m => m.Id == id).Paused));
        await Svc(async s => { await s.SetPausedAsync(id, Latest(id).Id, false); return 0; });
        Assert.Equal(SnapshotReason.Resume, Latest(id).Reason);
        Assert.Equal(240m, Latest(id).TotalUnits);
    }

    [Fact]
    public async Task Intake_is_clamped_at_zero()
    {
        var id = await NewMedAsync(perDay: 0, upp: 20, open: 3, sealedPackages: 0, mode: UsageMode.AsNeeded);
        await Svc(async s => { await s.IntakeAsync(id, 5); return 0; });
        Assert.Equal(0m, Latest(id).TotalUnits);
    }
}

public class MedicationWebTests : IClassFixture<AddonWebFactory>
{
    private readonly AddonWebFactory _f;
    public MedicationWebTests(AddonWebFactory f) => _f = f;

    private static readonly Regex TokenRx = new("name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"");

    private static async Task<string> TokenAsync(HttpClient c, string url)
    {
        var html = await c.GetStringAsync(url);
        return TokenRx.Match(html).Groups[1].Value;
    }

    private static FormUrlEncodedContent Form(string token, params (string, string)[] fields) =>
        new([new("__RequestVerificationToken", token), .. fields.Select(f => new KeyValuePair<string, string>(f.Item1, f.Item2))]);

    private async Task<int> EnsureProfileAsync(HttpClient c, string name)
    {
        var existing = _f.WithDb(db => db.Profiles.FirstOrDefault(p => p.Name == name)?.Id);
        if (existing is { } id) return id;
        var token = await TokenAsync(c, "/Settings/Profiles");
        var resp = await c.PostAsync("/Settings/Profiles?handler=Add", Form(token, ("name", name), ("color", "#43a047"), ("coverageDays", "")));
        Assert.Equal(HttpStatusCode.Redirect, resp.StatusCode);
        return _f.WithDb(db => db.Profiles.Single(p => p.Name == name).Id);
    }

    private static (string, string)[] NasalixFields(string profile) =>
    [
        ("Input.Profile", profile), ("Input.Name", "Nasalix"), ("Input.Strength", "50 µg"), ("Input.Type", "NasalSpray"),
        ("Input.UnitsPerPackage", "140"), ("Input.UsageMode", "PerDay"), ("Input.PerDay", "8"),
        ("Input.MinStockUnits", "0"), ("Input.OpenUnits", "100"), ("Input.SealedPackages", "1"), ("Input.TakenToday", "true"),
        ("Input.NotificationsEnabled", "true"),
    ];

    [Fact]
    public async Task Empty_state_without_profiles_links_to_profiles_and_new_redirects()
    {
        using var f = new AddonWebFactory();
        var html = await f.Client().GetStringAsync("/");
        Assert.Contains("/api/hassio_ingress/test/Settings/Profiles?next=new", html);
        // Antiforgery cookies get Path = PathBase and the test client does not put the prefix in its URLs: post without one.
        var c = f.Client(ingressPath: null);
        var resp = await c.GetAsync("/Medications/Edit");
        Assert.Equal(HttpStatusCode.Redirect, resp.StatusCode);
        Assert.Contains("/Settings/Profiles", resp.Headers.Location!.ToString());

        var token = await TokenAsync(c, "/Settings/Profiles?next=new");
        var add = await c.PostAsync("/Settings/Profiles?handler=Add&next=new", Form(token, ("name", "Alex"), ("color", "#1e88e5")));
        Assert.Equal(HttpStatusCode.Redirect, add.StatusCode);
        Assert.Contains("/Medications/Edit?profileId=", add.Headers.Location!.ToString());
        Assert.Contains("class=\"fab\"", await c.GetStringAsync("/"));
    }

    [Fact]
    public async Task New_medication_from_an_unknown_scan_gets_the_barcode_and_take_along_amount()
    {
        var c = _f.Client(ingressPath: null);
        var profileId = await EnsureProfileAsync(c, "Scanned");
        var html = await c.GetStringAsync($"/Medications/Edit?profileId={profileId}&barcode=5499999000017");
        Assert.Contains("value=\"05499999000017\"", html);
        var token = System.Text.RegularExpressions.Regex.Match(html, "name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"").Groups[1].Value;
        var fields = NasalixFields(profileId.ToString()).Where(f => f.Item1 != "Input.Name")
            .Append(("Input.Name", "Paracetamol")).Append(("Input.Barcode", "05499999000017")).Append(("Input.TakeAlongUnits", "141")).ToArray();
        var resp = await c.PostAsync("/Medications/Edit", Form(token, fields));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode); // 141 > 140 per package
        fields[^1] = ("Input.TakeAlongUnits", "10");
        resp = await c.PostAsync("/Medications/Edit", Form(token, fields));
        Assert.Equal(HttpStatusCode.Redirect, resp.StatusCode);
        var med = _f.WithDb(db => db.Medications.Single(m => m.ProfileId == profileId));
        Assert.Equal("05499999000017", med.Barcode);
        Assert.Equal(10m, med.TakeAlongUnits);
    }

    [Fact]
    public async Task Create_medication_through_the_form()
    {
        var c = _f.Client(ingressPath: null);
        var profileId = await EnsureProfileAsync(c, "Create");
        var token = await TokenAsync(c, $"/Medications/Edit?profileId={profileId}");
        var resp = await c.PostAsync("/Medications/Edit", Form(token, NasalixFields(profileId.ToString())));
        Assert.Equal(HttpStatusCode.Redirect, resp.StatusCode);
        var med = _f.WithDb(db => db.Medications.Include(m => m.Snapshots).Single(m => m.ProfileId == profileId));
        Assert.Equal(240m, med.Snapshots.Single().TotalUnits);
        Assert.Equal(MedicationType.NasalSpray, med.Type);

        var detail = await c.GetStringAsync(resp.Headers.Location!.ToString());
        Assert.Contains("Nasalix", detail);
    }

    [Fact]
    public async Task Create_with_new_profile_in_the_same_post_and_comma_decimals()
    {
        var c = _f.Client(ingressPath: null);
        await EnsureProfileAsync(c, "Existing");
        var token = await TokenAsync(c, "/Medications/Edit");
        var fields = NasalixFields(Web.Pages.Medications.EditModel.NewProfileValue)
            .Where(f => f.Item1 != "Input.PerDay").Append(("Input.PerDay", "0,5")).Append(("Input.NewProfileName", "Brand new")).ToArray();
        var resp = await c.PostAsync("/Medications/Edit", Form(token, fields));
        Assert.Equal(HttpStatusCode.Redirect, resp.StatusCode);
        var med = _f.WithDb(db => db.Medications.Include(m => m.Profile).Single(m => m.Profile!.Name == "Brand new"));
        Assert.Equal(0.5m, med.PerDay);
    }

    [Fact]
    public async Task Validation_errors_are_shown_and_nothing_is_saved()
    {
        var c = _f.Client(ingressPath: null);
        c.DefaultRequestHeaders.AcceptLanguage.ParseAdd("nl-BE");
        var profileId = await EnsureProfileAsync(c, "Validation");
        var token = await TokenAsync(c, "/Medications/Edit");
        var fields = NasalixFields(profileId.ToString()).Where(f => f.Item1 is not ("Input.Name" or "Input.UnitsPerPackage"))
            .Append(("Input.Name", "")).Append(("Input.UnitsPerPackage", "0")).ToArray();
        var resp = await c.PostAsync("/Medications/Edit", Form(token, fields));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var html = await resp.Content.ReadAsStringAsync();
        Assert.Contains("Verplicht", html);
        Assert.Contains("Moet groter zijn dan 0", html);
        Assert.False(_f.WithDb(db => db.Medications.Any(m => m.ProfileId == profileId)));
    }

    [Fact]
    public async Task Stale_usage_edit_returns_409_with_message()
    {
        var c = _f.Client(ingressPath: null);
        var profileId = await EnsureProfileAsync(c, "Conflict");
        var token = await TokenAsync(c, "/Medications/Edit");
        await c.PostAsync("/Medications/Edit", Form(token, NasalixFields(profileId.ToString())));
        var id = _f.WithDb(db => db.Medications.Single(m => m.ProfileId == profileId).Id);
        var html = await c.GetStringAsync($"/Medications/Edit?id={id}");
        var expected = Regex.Match(html, "name=\"Input.ExpectedSnapshotId\" value=\"(\\d+)\"").Groups[1].Value;
        if (expected == "") expected = Regex.Match(html, "value=\"(\\d+)\"[^>]*name=\"Input.ExpectedSnapshotId\"").Groups[1].Value;
        Assert.NotEqual("", expected);

        using (var scope = _f.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<MedicationService>().PurchaseAsync(id, 1);

        token = TokenRx.Match(html).Groups[1].Value;
        var fields = NasalixFields(profileId.ToString()).Where(f => f.Item1 != "Input.PerDay")
            .Append(("Input.PerDay", "4")).Append(("Input.Id", id.ToString())).Append(("Input.ExpectedSnapshotId", expected)).ToArray();
        var resp = await c.PostAsync("/Medications/Edit", Form(token, fields));
        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
        Assert.Contains("Someone else just changed this", await resp.Content.ReadAsStringAsync());
        Assert.Equal(8m, _f.WithDb(db => db.Medications.Single(m => m.Id == id).PerDay));
    }

    [Fact]
    public async Task Photo_post_writes_the_file_and_serves_it_with_caching()
    {
        var c = _f.Client(ingressPath: null);
        var profileId = await EnsureProfileAsync(c, "Photo");
        var token = await TokenAsync(c, "/Medications/Edit");
        await c.PostAsync("/Medications/Edit", Form(token, NasalixFields(profileId.ToString())));
        var id = _f.WithDb(db => db.Medications.Single(m => m.ProfileId == profileId).Id);

        byte[] jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3, 4, 5, 6, 0xFF, 0xD9];
        var content = new MultipartFormDataContent { { new ByteArrayContent(jpeg), "photo", "x.jpg" } };
        var req = new HttpRequestMessage(HttpMethod.Post, $"/Medications/Photo?id={id}") { Content = content };
        req.Headers.Add("RequestVerificationToken", token);
        var resp = await c.SendAsync(req);
        Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);
        Assert.Equal(jpeg, await File.ReadAllBytesAsync(Path.Combine(_f.DataDir, "photos", $"{id}.jpg")));
        Assert.True(_f.WithDb(db => db.Medications.Single(m => m.Id == id).HasPhoto));

        var get = await c.GetAsync($"/Medications/Photo?id={id}&v=1");
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        Assert.Equal("image/jpeg", get.Content.Headers.ContentType!.MediaType);
        Assert.Contains("immutable", get.Headers.CacheControl!.ToString());
        var again = new HttpRequestMessage(HttpMethod.Get, $"/Medications/Photo?id={id}&v=1");
        again.Headers.IfNoneMatch.Add(get.Headers.ETag!);
        Assert.Equal(HttpStatusCode.NotModified, (await c.SendAsync(again)).StatusCode);

        var bad = new HttpRequestMessage(HttpMethod.Post, $"/Medications/Photo?id={id}")
        {
            Content = new MultipartFormDataContent { { new ByteArrayContent("GIF89a.."u8.ToArray()), "photo", "x.gif" } },
        };
        bad.Headers.Add("RequestVerificationToken", token);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.SendAsync(bad)).StatusCode);
    }

    [Fact]
    public async Task Profile_with_medication_cannot_be_deleted_but_empty_one_can()
    {
        var c = _f.Client(ingressPath: null);
        var used = await EnsureProfileAsync(c, "Used");
        var token = await TokenAsync(c, "/Medications/Edit");
        await c.PostAsync("/Medications/Edit", Form(token, NasalixFields(used.ToString())));
        var empty = await EnsureProfileAsync(c, "Empty");

        token = await TokenAsync(c, "/Settings/Profiles");
        await c.PostAsync($"/Settings/Profiles?handler=Delete&id={used}", Form(token));
        await c.PostAsync($"/Settings/Profiles?handler=Delete&id={empty}", Form(token));
        Assert.True(_f.WithDb(db => db.Profiles.Any(p => p.Id == used)));
        Assert.False(_f.WithDb(db => db.Profiles.Any(p => p.Id == empty)));
    }

    [Fact]
    public async Task Profiles_can_be_reordered()
    {
        var c = _f.Client(ingressPath: null);
        var a = await EnsureProfileAsync(c, "Order A");
        var b = await EnsureProfileAsync(c, "Order B");
        var token = await TokenAsync(c, "/Settings/Profiles");
        await c.PostAsync($"/Settings/Profiles?handler=Move&id={b}&direction=-1", Form(token));
        var order = _f.WithDb(db => db.Profiles.OrderBy(p => p.SortOrder).Select(p => p.Id).ToList());
        Assert.True(order.IndexOf(b) < order.IndexOf(a));
    }
}
