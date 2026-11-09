using System.Globalization;
using System.Text;
using MedStock.Web.Data;
using MedStock.Web.Domain;
using MedStock.Web.Middleware;
using MedStock.Web.Services;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace MedStock.Web.Pages;

/// <summary>One line on the prescription screen.</summary>
public sealed record PrescriptionLine(MedicationRow Row, int Packages, int CoverageDays)
{
    public Medication Medication => Row.Medication;
    /// <summary>Shown at the top: something to ask for, or running out within the coverage period.</summary>
    public bool Expanded => !Medication.Paused && (Packages > 0 || Row.Stock.DaysRemaining <= CoverageDays);
}

/// <summary>
/// Prescription screen (§3.2): per profile, how many packages to ask the doctor for. "All" (the default when the overview
/// shows everyone) lists every profile; the text then has one block per profile that needs something. Over-the-counter
/// medication is not asked for: it is listed apart, to buy yourself.
/// </summary>
public class DoctorModel(MedStockDb db, IClock clock, CurrentUser user) : PageModel
{
    public const string AllValue = "all";
    public List<Profile> Profiles { get; private set; } = [];
    /// <summary>The chosen profile; null when there are no profiles or "All" is chosen.</summary>
    public Profile? Profile { get; private set; }
    public bool All { get; private set; }
    public List<PrescriptionLine> Lines { get; private set; } = [];
    /// <summary>Over the counter and something to buy.</summary>
    public List<PrescriptionLine> OverTheCounter { get; private set; } = [];
    public string ShareText { get; private set; } = "";

    public async Task OnGetAsync(string? profile)
    {
        Profiles = await db.Profiles.AsNoTracking().OrderBy(p => p.SortOrder).ThenBy(p => p.Id).ToListAsync();
        if (Profiles.Count == 0) return;
        var remembered = await db.UserSettings.AsNoTracking().Where(s => s.UserId == user.Id).Select(s => s.LastProfileFilter).FirstOrDefaultAsync();
        int? id = int.TryParse(profile, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
        Profile = Profiles.FirstOrDefault(p => p.Id == id);
        if (Profile is null)
        {
            All = Profiles.Count > 1 && (profile == AllValue || (profile is null && Profiles.All(p => p.Id != remembered)));
            if (!All) Profile = Profiles.FirstOrDefault(p => p.Id == remembered) ?? Profiles[0];
        }

        var today = clock.Today;
        var global = await db.GlobalSettings.AsNoTracking().SingleAsync();
        var order = Profiles.Select(p => p.Id).ToList();
        var lines = (await MedicationQueries.LoadAsync(db, today))
            .Where(r => All || r.Medication.ProfileId == Profile!.Id)
            .Select(r =>
            {
                var coverage = PrescriptionCalculator.CoverageDays(r.Medication.CoverageDays, r.Profile.CoverageDays, global.CoverageDays);
                return new PrescriptionLine(r, PrescriptionCalculator.PackagesNeeded(StockInput.From(r.Medication, r.Snapshot), today, coverage), coverage);
            })
            .OrderBy(l => order.IndexOf(l.Medication.ProfileId)) // stable: within a profile still by state, days, name
            .ToList();
        Lines = lines.Where(l => !l.Medication.OverTheCounter).ToList();
        OverTheCounter = lines.Where(l => l.Medication.OverTheCounter && l.Packages > 0).ToList();

        var c = CultureInfo.CurrentUICulture;
        if (!All)
        {
            ShareText = BuildShareText(Profile!, Lines, today, c);
            return;
        }
        var blocks = Profiles
            .Select(p => (Profile: p, Lines: Lines.Where(l => l.Medication.ProfileId == p.Id).ToList()))
            .Where(x => x.Lines.Any(l => l.Packages > 0))
            .Select(x => BuildShareText(x.Profile, x.Lines, today, c))
            .ToList();
        ShareText = blocks.Count > 0
            ? string.Join("\n\n", blocks)
            : string.Join("\n", Profiles.Select(p => BuildShareText(p, [], today, c)));
    }

    /// <summary>"(nog 20 dagen)" for daily use, "(laatste dosis za 28 nov)" for a weekly pattern.</summary>
    public static string Status(MedicationRow r, CultureInfo c) =>
        r.Medication.UsageMode == UsageMode.Weekly && r.Stock.State != StockState.Empty && r.LastDose(c) is { } lastDose
            ? lastDose
            : r.Badge(c);

    public static string BuildShareText(Profile profile, IEnumerable<PrescriptionLine> lines, DateOnly today, CultureInfo c)
    {
        var sb = new StringBuilder();
        sb.Append(Texts.Format("Doctor_ShareTitle", c, profile.Name, Texts.LongDate(today, c)));
        foreach (var l in lines.Where(l => l.Packages > 0))
        {
            var m = l.Medication;
            var name = string.IsNullOrWhiteSpace(m.Strength) ? m.Name : $"{m.Name} {m.Strength}";
            sb.Append('\n').Append($"- {name}: {l.Packages} {Texts.Package(m.Type, l.Packages, c)} ({Status(l.Row, c)})");
        }
        return sb.ToString();
    }
}
