using MedStock.Web.Data;
using MedStock.Web.Domain;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;

namespace MedStock.Web.Pages.Settings;

public class ProfilesModel(MedStockDb db, IStringLocalizer<SharedResource> l, Services.SensorSignal sensors, ILogger<ProfilesModel> log) : PageModel
{
    public static readonly string[] Palette =
        ["#1e88e5", "#d81b60", "#43a047", "#fb8c00", "#8e24aa", "#00897b", "#6d4c41", "#546e7a"];

    public List<Profile> Profiles { get; private set; } = [];
    public HashSet<int> InUse { get; private set; } = [];
    public int GlobalCoverageDays { get; private set; }
    public string? Error { get; private set; }
    public int? ErrorProfileId { get; private set; }

    /// <summary>"new": after adding a profile continue straight to a new medication in that profile (empty-state flow).</summary>
    [BindProperty(SupportsGet = true)] public string? Next { get; set; }

    public Task OnGetAsync() => LoadAsync();

    public async Task<IActionResult> OnPostAddAsync(string? name, string? color, string? coverageDays)
    {
        name = name?.Trim();
        if (await ValidateAsync(null, name, coverageDays) is { } error) return await Fail(error, null);
        var profile = new Profile
        {
            Name = name!,
            Color = Palette.Contains(color) ? color! : Palette[await db.Profiles.CountAsync() % Palette.Length],
            SortOrder = (await db.Profiles.MaxAsync(p => (int?)p.SortOrder) ?? -1) + 1,
            CoverageDays = ParseDays(coverageDays),
        };
        db.Profiles.Add(profile);
        await db.SaveChangesAsync();
        log.LogInformation("Profile {ProfileId} created", profile.Id);
        return Next == "new"
            ? RedirectToPage("/Medications/Edit", new { profileId = profile.Id })
            : RedirectToPage();
    }

    public async Task<IActionResult> OnPostSaveAsync(int id, string? name, string? color, string? coverageDays)
    {
        var profile = await db.Profiles.FindAsync(id);
        if (profile is null) return NotFound();
        name = name?.Trim();
        if (await ValidateAsync(id, name, coverageDays) is { } error) return await Fail(error, id);
        profile.Name = name!;
        if (Palette.Contains(color)) profile.Color = color!;
        profile.CoverageDays = ParseDays(coverageDays);
        await db.SaveChangesAsync();
        sensors.Request(); // sensor names include the profile
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostMoveAsync(int id, int direction)
    {
        var ordered = await db.Profiles.OrderBy(p => p.SortOrder).ThenBy(p => p.Id).ToListAsync();
        var i = ordered.FindIndex(p => p.Id == id);
        var j = i + Math.Sign(direction);
        if (i >= 0 && j >= 0 && j < ordered.Count) (ordered[i], ordered[j]) = (ordered[j], ordered[i]);
        for (var k = 0; k < ordered.Count; k++) ordered[k].SortOrder = k;
        await db.SaveChangesAsync();
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostDeleteAsync(int id)
    {
        var profile = await db.Profiles.FindAsync(id);
        if (profile is null) return RedirectToPage();
        if (await db.Medications.AnyAsync(m => m.ProfileId == id)) return await Fail(l["Profile_HasMedication"], id);
        db.Profiles.Remove(profile);
        await db.SaveChangesAsync();
        log.LogInformation("Profile {ProfileId} deleted", id);
        return RedirectToPage();
    }

    internal static int? ParseDays(string? raw) => int.TryParse(raw?.Trim(), out var d) ? d : null;

    private async Task<string?> ValidateAsync(int? id, string? name, string? coverageDays)
    {
        if (string.IsNullOrEmpty(name)) return $"{l["Profile_Name"]}: {l["Val_Required"]}";
        if (name.Length > 50) return $"{l["Profile_Name"]}: {l["Val_MaxLength", 50]}";
        if (!string.IsNullOrWhiteSpace(coverageDays) && ParseDays(coverageDays) is not (>= 0 and <= 3650))
            return $"{l["CoverageDays"]}: {l["Val_Range"]}";
        var lower = name.ToLower();
        if (await db.Profiles.AnyAsync(p => p.Id != id && p.Name.ToLower() == lower)) return l["Profile_NameExists"];
        return null;
    }

    private async Task<IActionResult> Fail(string error, int? profileId)
    {
        Error = error;
        ErrorProfileId = profileId;
        await LoadAsync();
        return Page();
    }

    private async Task LoadAsync()
    {
        Profiles = await db.Profiles.AsNoTracking().OrderBy(p => p.SortOrder).ThenBy(p => p.Id).ToListAsync();
        InUse = (await db.Medications.Select(m => m.ProfileId).Distinct().ToListAsync()).ToHashSet();
        GlobalCoverageDays = (await db.GlobalSettings.AsNoTracking().SingleAsync()).CoverageDays;
    }
}
