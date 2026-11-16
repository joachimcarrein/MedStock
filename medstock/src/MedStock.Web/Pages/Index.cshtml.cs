using MedStock.Web.Data;
using MedStock.Web.Domain;
using MedStock.Web.Middleware;
using MedStock.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace MedStock.Web.Pages;

/// <summary>Overview (§3.3): cards sorted by urgency, filter chips per profile remembered per HA user.</summary>
public class IndexModel(MedStockDb db, IClock clock, CurrentUser user, AppPaths paths) : PageModel
{
    public List<Profile> Profiles { get; private set; } = [];
    public List<MedicationRow> Rows { get; private set; } = [];
    public int? Filter { get; private set; }
    public Dictionary<int, long> PhotoVersions { get; } = [];
    /// <summary>Today's intake moments still to come (time after now), within the profile filter, earliest first.</summary>
    public List<TodayMoment> Today { get; private set; } = [];

    public async Task OnGetAsync()
    {
        Profiles = await db.Profiles.AsNoTracking().OrderBy(p => p.SortOrder).ThenBy(p => p.Id).ToListAsync();
        if (Profiles.Count == 0) return;
        var saved = await db.UserSettings.AsNoTracking().Where(s => s.UserId == user.Id).Select(s => s.LastProfileFilter).FirstOrDefaultAsync();
        Filter = Profiles.Any(p => p.Id == saved) ? saved : null; // a deleted profile falls back to "all"
        Rows = (await MedicationQueries.LoadAsync(db, clock.Today))
            .Where(r => Filter is null || r.Medication.ProfileId == Filter).ToList();
        Today = TodayMoments(Rows, clock.Now);
        foreach (var r in Rows.Where(r => r.Medication.HasPhoto))
            PhotoVersions[r.Medication.Id] = Medications.PhotoModel.Version(paths, r.Medication.Id);
    }

    public static List<TodayMoment> TodayMoments(IReadOnlyList<MedicationRow> rows, DateTime now)
    {
        var today = DateOnly.FromDateTime(now);
        var time = TimeOnly.FromDateTime(now);
        var meds = rows.Select(r => r.Medication).ToList();
        return ReminderService.MomentsOn(meds, today)
            .Where(k => k.Time > time)
            .GroupBy(k => k.Time)
            .OrderBy(g => g.Key)
            .Select(g => new TodayMoment(g.Key, rows
                .Select(r => (Row: r, Amount: ReminderService.AmountAt(r.Medication, today, g.Key)))
                .Where(x => x.Amount > 0)
                .OrderBy(x => x.Row.Profile.SortOrder).ThenBy(x => x.Row.Medication.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList()))
            .Where(m => m.Items.Count > 0)
            .ToList();
    }

    /// <summary>Chip tap: remember the choice server-side (the Companion app switches origins, so no localStorage).</summary>
    public async Task<IActionResult> OnPostFilterAsync(int? profileId)
    {
        var settings = await db.UserSettings.FirstOrDefaultAsync(s => s.UserId == user.Id);
        if (settings is not null)
        {
            settings.LastProfileFilter = profileId;
            await db.SaveChangesAsync();
        }
        // Url.Content keeps the trailing slash: the HA ingress route for the app root is ".../<token>/".
        return Redirect(Url.Content("~/"));
    }
}

/// <summary>One intake moment of today with the medication due then (and the amount).</summary>
public sealed record TodayMoment(TimeOnly Time, List<(MedicationRow Row, decimal Amount)> Items);
