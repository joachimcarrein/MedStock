using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using MedStock.Web.Domain;
using MedStock.Web.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace MedStock.Tests;

public sealed class FakeImageSearch : IImageSearch
{
    public List<string> Queries { get; } = [];
    public bool Unavailable { get; set; }
    public byte[] Picture { get; set; } = [0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3, 0xFF, 0xD9];

    public Task<IReadOnlyList<ImageHit>> SearchAsync(string query, int offset, CancellationToken ct = default)
    {
        Queries.Add(query);
        if (Unavailable) throw new ImageSearchUnavailableException(new HttpRequestException());
        return Task.FromResult<IReadOnlyList<ImageHit>>(
            [new ImageHit("https://thumb.wikimedia.org/wikipedia/commons/thumb/a/ab/X.jpg/400px-X.jpg", "X.jpg", "https://commons.wikimedia.org/wiki/File:X.jpg")]);
    }

    public Task<byte[]?> DownloadAsync(string url, CancellationToken ct = default) =>
        Task.FromResult(WikimediaImageSearch.IsAllowed(url) ? Picture : null);
}

public class ExportAndImageSearchTests : IClassFixture<AddonWebFactory>
{
    private readonly AddonWebFactory _f;
    private readonly int _medId;

    public ExportAndImageSearchTests(AddonWebFactory f)
    {
        _f = f;
        _medId = _f.WithDb(db =>
        {
            var existing = db.Medications.FirstOrDefault(m => m.Name == "Nasalix");
            if (existing is not null) return existing.Id;
            var m = new Medication
            {
                Profile = new Profile { Name = "Husband", CoverageDays = 60 }, Name = "Nasalix", Strength = "50 µg",
                Type = MedicationType.NasalSpray, UnitsPerPackage = 140, PerDay = 8,
                Snapshots =
                [
                    new Snapshot { Date = T.D("2026-11-01"), TotalUnits = 100 },
                    new Snapshot { Date = T.D("2026-11-07"), TotalUnits = 240, Reason = SnapshotReason.Recount },
                ],
            };
            db.Medications.Add(m);
            db.Medications.Add(new Medication { Profile = m.Profile, Name = "Old", Archived = true, UnitsPerPackage = 1,
                Snapshots = [new Snapshot { Date = T.D("2026-10-01"), TotalUnits = 3 }] });
            db.SaveChanges();
            return m.Id;
        });
    }

    private HttpClient Client() => _f.Client(ingressPath: null);

    private static string Token(string html) =>
        Regex.Match(html, "name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"").Groups[1].Value;

    [Fact]
    public async Task Export_downloads_json_with_profiles_medication_and_current_stock()
    {
        var resp = await Client().GetAsync("/Settings/Export");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("application/json", resp.Content.Headers.ContentType!.MediaType);
        Assert.StartsWith("medstock-export-2026-11-17", resp.Content.Headers.ContentDisposition!.FileName!.Trim('"'));

        var json = JsonNode.Parse(await resp.Content.ReadAsStringAsync())!;
        Assert.Equal(1, (int?)json["format"]);
        Assert.Equal(90, (int?)json["settings"]!["coverageDays"]);
        var nasalix = json["medications"]!.AsArray().Single(m => (string?)m!["name"] == "Nasalix")!;
        Assert.Equal("50 µg", (string?)nasalix["strength"]); // readable, not µ
        Assert.Equal("NasalSpray", (string?)nasalix["type"]);
        Assert.Equal("2026-11-07", (string?)nasalix["stock"]!["date"]); // latest snapshot only
        Assert.Equal(240m, (decimal?)nasalix["stock"]!["totalUnits"]);
        Assert.Equal(7, nasalix["weekly"]!.AsArray().Count);
        Assert.Contains(json["medications"]!.AsArray(), m => (string?)m!["name"] == "Old" && (bool?)m["archived"] == true);
        Assert.Contains(json["profiles"]!.AsArray(), p => (string?)p!["name"] == "Husband" && (int?)p["coverageDays"] == 60);
    }

    [Theory]
    [InlineData("https://thumb.wikimedia.org/wikipedia/commons/thumb/a/ab/X.jpg/400px-X.jpg", true)]
    [InlineData("https://upload.wikimedia.org/wikipedia/commons/a/ab/X.jpg", true)]
    [InlineData("http://thumb.wikimedia.org/x.jpg", false)]
    [InlineData("https://thumb.wikimedia.org.evil.example/x.jpg", false)]
    [InlineData("https://user@thumb.wikimedia.org/x.jpg", false)]
    [InlineData("https://thumb.wikimedia.org:8443/x.jpg", false)]
    [InlineData("http://supervisor/core/api/states", false)]
    [InlineData("file:///data/medstock.db", false)]
    [InlineData(null, false)]
    public void Only_wikimedia_image_urls_are_allowed(string? url, bool allowed) =>
        Assert.Equal(allowed, WikimediaImageSearch.IsAllowed(url));

    [Fact]
    public async Task Search_page_sends_nothing_until_the_user_taps_search()
    {
        var search = (FakeImageSearch)_f.Services.GetService(typeof(IImageSearch))!;
        var before = search.Queries.Count;
        var html = await Client().GetStringAsync($"/Medications/ImageSearch?id={_medId}");
        Assert.Contains("value=\"Nasalix\"", html);
        Assert.Equal(before, search.Queries.Count);

        html = await Client().GetStringAsync($"/Medications/ImageSearch?id={_medId}&go=true&q=Saline");
        Assert.Equal("Saline", search.Queries.Last());
        Assert.Contains("400px-X.jpg", html);
    }

    [Fact]
    public async Task Choosing_a_picture_stores_it_as_the_photo_and_foreign_urls_are_refused()
    {
        var c = Client();
        var html = await c.GetStringAsync($"/Medications/ImageSearch?id={_medId}&go=true");
        var bad = await c.PostAsync($"/Medications/ImageSearch?id={_medId}&handler=Use", new FormUrlEncodedContent(
            [new("__RequestVerificationToken", Token(html)), new("url", "http://supervisor/core/api/states")]));
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);

        var ok = await c.PostAsync($"/Medications/ImageSearch?id={_medId}&handler=Use", new FormUrlEncodedContent(
            [new("__RequestVerificationToken", Token(html)), new("url", "https://thumb.wikimedia.org/wikipedia/commons/thumb/a/ab/X.jpg/400px-X.jpg")]));
        Assert.Equal(HttpStatusCode.Redirect, ok.StatusCode);
        Assert.True(File.Exists(Path.Combine(_f.DataDir, "photos", $"{_medId}.jpg")));
        Assert.True(_f.WithDb(db => db.Medications.Single(m => m.Id == _medId).HasPhoto));
    }

    [Fact]
    public async Task Unreachable_search_shows_a_message()
    {
        var search = (FakeImageSearch)_f.Services.GetService(typeof(IImageSearch))!;
        search.Unavailable = true;
        try
        {
            var html = await Client().GetStringAsync($"/Medications/ImageSearch?id={_medId}&go=true");
            Assert.Contains("Wikimedia Commons cannot be reached", html);
        }
        finally { search.Unavailable = false; }
    }

    [Fact]
    public async Task Wikimedia_response_is_parsed_in_order_and_filtered()
    {
        const string body = """
        {"query":{"pages":[
          {"index":2,"title":"File:B.png","imageinfo":[{"mime":"image/png","thumburl":"https://thumb.wikimedia.org/b.png","descriptionurl":"https://commons.wikimedia.org/wiki/File:B.png"}]},
          {"index":1,"title":"File:A.jpg","imageinfo":[{"mime":"image/jpeg","thumburl":"https://thumb.wikimedia.org/a.jpg","descriptionurl":"d"}]},
          {"index":3,"title":"File:C.svg","imageinfo":[{"mime":"image/svg+xml","thumburl":"https://thumb.wikimedia.org/c.png"}]},
          {"index":4,"title":"File:D.jpg","imageinfo":[{"mime":"image/jpeg","thumburl":"https://evil.example/d.jpg"}]}
        ]}}
        """;
        string? requested = null;
        var http = new HttpClient(new StubHandler(req =>
        {
            requested = req.RequestUri!.AbsoluteUri;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }));
        var hits = await new WikimediaImageSearch(http, NullLogger<WikimediaImageSearch>.Instance).SearchAsync("Nasalix 50 µg", 12);
        Assert.Equal(["A.jpg", "B.png"], hits.Select(h => h.Title));
        Assert.Contains("gsroffset=12", requested);
        Assert.Contains("gsrsearch=Nasalix%2050%20%C2%B5g", requested);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(respond(request));
    }
}
