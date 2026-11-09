using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using MedStock.Web.Domain;

namespace MedStock.Tests;

/// <summary>Import of a JSON export into another MedStock: adds, never overwrites.</summary>
public class ImportTests
{
    private static string Token(string html) =>
        Regex.Match(html, "name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"").Groups[1].Value;

    private static async Task<(HttpStatusCode Status, string Html)> PostAsync(AddonWebFactory f, byte[] file, bool applySettings = false)
    {
        var c = f.Client("imp", ingressPath: null);
        c.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en");
        var token = Token(await c.GetStringAsync("/Settings/Import"));
        var form = new MultipartFormDataContent { { new StringContent(token), "__RequestVerificationToken" } };
        var content = new ByteArrayContent(file);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        form.Add(content, "file", "medstock-export.json");
        if (applySettings) form.Add(new StringContent("true"), "applySettings");
        var resp = await c.PostAsync("/Settings/Import", form);
        return (resp.StatusCode, WebUtility.HtmlDecode(await resp.Content.ReadAsStringAsync()));
    }

    [Fact]
    public async Task Export_of_one_household_imports_into_another_and_a_second_import_skips_everything()
    {
        byte[] export;
        using (var source = new AddonWebFactory())
        {
            source.WithDb(db =>
            {
                var p = new Profile { Name = "Husband", Color = "#e53935", CoverageDays = 60 };
                db.Medications.Add(new Medication
                {
                    Profile = p, Name = "Nasalix", Strength = "50 µg", Type = MedicationType.NasalSpray, UnitsPerPackage = 140, PerDay = 8,
                    Barcode = "5410000000001",
                    Snapshots = [new Snapshot { Date = T.D("2026-11-07"), TotalUnits = 240 }],
                });
                db.Medications.Add(new Medication
                {
                    Profile = p, Name = "Weekject", Type = MedicationType.Injection, UnitsPerPackage = 4, UsageMode = UsageMode.Weekly,
                    Weekly = T.Saturday, Archived = true, Snapshots = [new Snapshot { Date = T.D("2026-11-09"), TotalUnits = 3 }],
                });
                var g = db.GlobalSettings.Single();
                g.CoverageDays = 120;
                return db.SaveChanges();
            });
            export = await source.Client("exp", ingressPath: null).GetByteArrayAsync("/Settings/Export");
        }

        using var target = new AddonWebFactory();
        target.WithDb(db =>
        {
            db.Profiles.Add(new Profile { Name = "husband", Color = "#43a047" }); // matched by name, case-insensitive
            return db.SaveChanges();
        });

        var (status, html) = await PostAsync(target, export, applySettings: true);
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Contains("2 medications imported, 0 profiles created.", html);
        Assert.Contains("The general settings were taken over.", html);

        target.WithDb(db =>
        {
            var profile = Assert.Single(db.Profiles.ToList());
            Assert.Equal("#43a047", profile.Color); // the existing profile is not changed
            var nasalix = db.Medications.Single(m => m.Name == "Nasalix");
            Assert.Equal("50 µg", nasalix.Strength);
            Assert.Equal("5410000000001", nasalix.Barcode);
            var s = db.Snapshots.Single(x => x.MedicationId == nasalix.Id);
            Assert.Equal(T.D("2026-11-07"), s.Date); // exported stock becomes the first count
            Assert.Equal(240m, s.TotalUnits);
            Assert.Equal(SnapshotReason.Initial, s.Reason);
            Assert.True(db.Medications.Single(m => m.Name == "Weekject").Archived);
            Assert.Equal(120, db.GlobalSettings.Single().CoverageDays);
            return 0;
        });

        (status, html) = await PostAsync(target, export);
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Contains("0 medications imported", html);
        Assert.Contains("2 skipped because they already exist.", html);
        Assert.Equal(2, target.WithDb(db => db.Medications.Count()));
    }

    [Fact]
    public async Task Invalid_files_are_refused_and_invalid_entries_skipped()
    {
        using var f = new AddonWebFactory();
        var (status, html) = await PostAsync(f, Encoding.UTF8.GetBytes("{ not json"));
        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Contains("not a MedStock export", html);

        (status, html) = await PostAsync(f, Encoding.UTF8.GetBytes("""{"format":2,"profiles":[],"medications":[]}"""));
        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Contains("newer MedStock version", html);

        var doc = """
            {"format":1,"settings":{"coverageDays":90,"defaultAlertDays":14,"notificationTime":"09:00"},
             "profiles":[{"id":7,"name":"Medicine owner","color":"#nope","sortOrder":0}],
             "medications":[
               {"id":1,"profileId":7,"name":"Medicine A","type":"Pill","unitsPerPackage":20,"usageMode":"PerDay","perDay":1,
                "weekly":[0,0,0,0,0,0,0],"minStockUnits":0,"notificationsEnabled":true,"paused":false,"archived":false,
                "stock":{"date":"2026-11-01","totalUnits":10}},
               {"id":2,"profileId":7,"name":"Bad type","type":"Rocket","unitsPerPackage":20,"usageMode":"PerDay","perDay":1,
                "weekly":[0,0,0,0,0,0,0],"minStockUnits":0,"notificationsEnabled":true,"paused":false,"archived":false},
               {"id":3,"profileId":99,"name":"No profile","type":"Pill","unitsPerPackage":20,"usageMode":"PerDay","perDay":1,
                "weekly":[0,0,0,0,0,0,0],"minStockUnits":0,"notificationsEnabled":true,"paused":false,"archived":false}
             ]}
            """;
        (status, html) = await PostAsync(f, Encoding.UTF8.GetBytes(doc));
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Contains("1 medications imported, 1 profiles created.", html);
        Assert.Contains("2 entries skipped", html);
        Assert.DoesNotContain("general settings were taken over", html);
        Assert.Contains(f.WithDb(db => db.Profiles.Single().Color), MedStock.Web.Pages.Settings.ProfilesModel.Palette); // invalid colour replaced
    }
}
