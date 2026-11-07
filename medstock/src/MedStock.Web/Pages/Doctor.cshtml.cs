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

/// <summary>Prescription screen (§3.2): per profile, how many packages to ask the doctor for.</summary>
public class DoctorModel(MedStockDb db, IClock clock, CurrentUser user) : PageModel
{
    public List<Profile> Profiles { get; private set; } = [];
    public Profile? Profile { get; private set; }
    public List<PrescriptionLine> Lines { get; private set; } = [];
    public string ShareText { get; private set; } = "";

    public async Task OnGetAsync(int? profile)
    {
        Profiles = await db.Profiles.AsNoTracking().OrderBy(p => p.SortOrder).ThenBy(p => p.Id).ToListAsync();
        if (Profiles.Count == 0) return;
        var remembered = await db.UserSettings.AsNoTracking().Where(s => s.UserId == user.Id).Select(s => s.LastProfileFilter).FirstOrDefaultAsync();
        Profile = Profiles.FirstOrDefault(p => p.Id == profile) ?? Profiles.FirstOrDefault(p => p.Id == remembered) ?? Profiles[0];

        var today = clock.Today;
        var global = await db.GlobalSettings.AsNoTracking().SingleAsync();
        var selected = Profile;
        Lines = (await MedicationQueries.LoadAsync(db, today))
            .Where(r => r.Medication.ProfileId == selected.Id)
            .Select(r =>
            {
                var coverage = PrescriptionCalculator.CoverageDays(r.Medication.CoverageDays, selected.CoverageDays, global.CoverageDays);
                return new PrescriptionLine(r, PrescriptionCalculator.PackagesNeeded(StockInput.From(r.Medication, r.Snapshot), today, coverage), coverage);
            })
            .ToList(); // already sorted by state, days, name
        ShareText = BuildShareText(selected, Lines, today, CultureInfo.CurrentUICulture);
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
