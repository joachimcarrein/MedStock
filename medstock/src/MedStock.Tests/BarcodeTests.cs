using System.Net;
using System.Text.RegularExpressions;
using MedStock.Web.Domain;
using MedStock.Web.Services;

namespace MedStock.Tests;

/// <summary>Barcodes: stored as the product number, scan from the overview, link an unknown code.</summary>
public class BarcodeTests : IClassFixture<AddonWebFactory>
{
    private readonly AddonWebFactory _f;
    public BarcodeTests(AddonWebFactory f) => _f = f;

    [Theory]
    [InlineData("5410000000017", "05410000000017")]                               // EAN-13
    [InlineData("96385074", "00000096385074")]                                    // EAN-8
    [InlineData("012345678905", "00012345678905")]                                // UPC-A
    [InlineData("05410000000017", "05410000000017")]                              // GTIN-14
    [InlineData("0105410000000017211234567890\u001d17280131", "05410000000017")]  // GS1 DataMatrix with serial and expiry
    [InlineData("]d20105410000000017219876", "05410000000017")]                    // with symbology identifier
    [InlineData("(01)05410000000017(21)ABC", "05410000000017")]                   // human readable form
    [InlineData("  https://example.org/x  ", "https://example.org/x")]
    [InlineData("   ", null)]
    [InlineData(null, null)]
    public void Normalize_keeps_the_product_number(string? raw, string? expected) => Assert.Equal(expected, Barcodes.Normalize(raw));

    private (int A, int B) Seed(string name) => _f.WithDb(db =>
    {
        var p1 = new Profile { Name = name + "-1" };
        var p2 = new Profile { Name = name + "-2" };
        var a = new Medication
        {
            Profile = p1, Name = name, Type = MedicationType.NasalSpray, UnitsPerPackage = 140, PerDay = 8,
            Snapshots = [new Snapshot { Date = T.D("2026-11-17"), TotalUnits = 100 }],
        };
        var b = new Medication
        {
            Profile = p2, Name = name, Type = MedicationType.NasalSpray, UnitsPerPackage = 140, PerDay = 8,
            Snapshots = [new Snapshot { Date = T.D("2026-11-17"), TotalUnits = 100 }],
        };
        db.Medications.AddRange(a, b);
        db.SaveChanges();
        return (a.Id, b.Id);
    });

    private static string Token(string html) =>
        Regex.Match(html, "name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"").Groups[1].Value;

    private HttpClient Client()
    {
        var c = _f.Client("bc", ingressPath: null);
        c.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en");
        return c;
    }

    [Fact]
    public async Task Detail_saves_the_normalized_code_and_the_scan_opens_that_medication()
    {
        var (a, b) = Seed("Spray");
        var c = Client();
        var html = await c.GetStringAsync($"/Medications/Details?id={a}");
        Assert.Contains("data-scan=\"fill\"", html);
        Assert.Contains("lib/zxing/zxing_reader.wasm", html);

        var resp = await c.PostAsync($"/Medications/Details?id={a}&handler=Barcode", new FormUrlEncodedContent(
            [new("__RequestVerificationToken", Token(html)), new("barcode", "0105411111111118215550\u001d17280131")]));
        Assert.Equal(HttpStatusCode.Redirect, resp.StatusCode);
        Assert.Equal("05411111111118", _f.WithDb(db => db.Medications.Single(m => m.Id == a).Barcode));

        // Another box of the same product (other serial number) opens the detail, ready for +1.
        resp = await c.GetAsync("/Medications/Scan?code=" + Uri.EscapeDataString("0105411111111118219999"));
        Assert.Equal(HttpStatusCode.Redirect, resp.StatusCode);
        Assert.Contains($"id={a}", resp.Headers.Location!.ToString());
        Assert.Contains("done=scanned", resp.Headers.Location!.ToString());
        var detail = WebUtility.HtmlDecode(await c.GetStringAsync(resp.Headers.Location!.ToString()));
        Assert.Contains("Scanned. Bought one? Tap +1 bottle.", detail);

        // The same product in a second profile: choose.
        _f.WithDb(db =>
        {
            db.Medications.Single(m => m.Id == b).Barcode = "05411111111118";
            return db.SaveChanges();
        });
        resp = await c.GetAsync("/Medications/Scan?code=5411111111118");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var choose = await resp.Content.ReadAsStringAsync();
        Assert.Contains($"id={a}", choose);
        Assert.Contains($"id={b}", choose);

        // Empty value removes the code.
        resp = await c.PostAsync($"/Medications/Details?id={a}&handler=Barcode", new FormUrlEncodedContent(
            [new("__RequestVerificationToken", Token(html)), new("barcode", "")]));
        Assert.Null(_f.WithDb(db => db.Medications.Single(m => m.Id == a).Barcode));
    }

    [Fact]
    public async Task Unknown_code_can_be_linked_to_a_medication()
    {
        var (a, _) = Seed("Drops");
        var c = Client();
        var resp = await c.GetAsync("/Medications/Scan?code=4006381333931");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var html = WebUtility.HtmlDecode(await resp.Content.ReadAsStringAsync());
        Assert.Contains("Code 04006381333931 is not linked yet", html);

        resp = await c.PostAsync("/Medications/Scan?handler=Link", new FormUrlEncodedContent(
            [new("__RequestVerificationToken", Token(html)), new("id", a.ToString()), new("code", "04006381333931")]));
        Assert.Equal(HttpStatusCode.Redirect, resp.StatusCode);
        Assert.Equal("04006381333931", _f.WithDb(db => db.Medications.Single(m => m.Id == a).Barcode));

        Assert.Equal(HttpStatusCode.Redirect, (await c.GetAsync("/Medications/Scan?code=%20")).StatusCode); // nothing scanned
        var overview = await c.GetStringAsync("/");
        Assert.Contains("data-scan=\"go\"", overview);
    }
}
