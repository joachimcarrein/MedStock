using System.Globalization;
using System.Text;
using MedStock.Web.Data;
using MedStock.Web.Domain;
using MedStock.Web.Middleware;
using MedStock.Web.Services;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace MedStock.Web.Pages;

public sealed record VacationLine(MedicationRow Row, VacationNeed? Need);

/// <summary>Trip planner: what to take along and what to buy before leaving.</summary>
public class VacationModel(MedStockDb db, IClock clock, CurrentUser user) : PageModel
{
    public const int MaxDays = 365;
    public List<Profile> Profiles { get; private set; } = [];
    public int? ProfileId { get; private set; }
    public DateOnly From { get; private set; }
    public int Days { get; private set; } = 7;
    public bool Calculated { get; private set; }
    public bool Invalid { get; private set; }
    public List<VacationLine> Lines { get; private set; } = [];
    public string ShareText { get; private set; } = "";

    public async Task OnGetAsync(DateOnly? from, int? days, string? profile)
    {
        var today = clock.Today;
        Profiles = await db.Profiles.AsNoTracking().OrderBy(p => p.SortOrder).ThenBy(p => p.Id).ToListAsync();
        if (profile is null)
            ProfileId = await db.UserSettings.AsNoTracking().Where(s => s.UserId == user.Id).Select(s => s.LastProfileFilter).FirstOrDefaultAsync();
        else if (int.TryParse(profile, NumberStyles.None, CultureInfo.InvariantCulture, out var id))
            ProfileId = id;
        if (Profiles.All(p => p.Id != ProfileId)) ProfileId = null; // "everyone"

        From = from ?? today.AddDays(1);
        Days = days ?? 7;
        if (from is null && days is null) return;
        if (From < today || Days is < 1 or > MaxDays)
        {
            Invalid = true;
            Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        Calculated = true;
        var departure = From;
        var length = Days;
        Lines = (await MedicationQueries.LoadAsync(db, today))
            .Where(r => ProfileId is null || r.Medication.ProfileId == ProfileId)
            .Select(r => new VacationLine(r, PrescriptionCalculator.Vacation(StockInput.From(r.Medication, r.Snapshot), today, departure, length,
                PrescriptionCalculator.TakePortion(r.Medication))))
            .OrderBy(l => l.Need is null ? 1 : 0).ThenByDescending(l => l.Need?.BuyPackages ?? 0)
            .ThenBy(l => l.Row.Medication.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        ShareText = BuildShareText(Lines, departure, length, ProfileId is null, CultureInfo.CurrentUICulture);
    }

    public static string Name(Medication m) => string.IsNullOrWhiteSpace(m.Strength) ? m.Name : $"{m.Name} {m.Strength}";

    /// <summary>"take 7 pills", "take 2 bottles (112 doses needed)" or "take 1 strip of 10 (7 pills needed)".</summary>
    public static string Take(VacationLine l, CultureInfo c)
    {
        var m = l.Row.Medication;
        var n = l.Need!;
        var units = Texts.UnitsCount(m.Type, Math.Ceiling(n.NeedUnits), c);
        if (n.Portion == 1) return Texts.Format("Vacation_Take", c, units);
        var what = n.Portion == m.UnitsPerPackage
            ? $"{n.TakePortions} {Texts.Package(m.Type, n.TakePortions, c)}"
            : $"{n.TakePortions} {Texts.Format(n.TakePortions == 1 ? "Strip_One" : "Strip_Other", c, n.Portion.ToString("0.##", c))}";
        return Texts.Format("Vacation_TakePortion", c, what, units);
    }

    /// <summary>"buy 1 bottle before Sat 20 Dec" or "enough in stock".</summary>
    public static string Buy(VacationLine l, DateOnly departure, CultureInfo c)
    {
        var m = l.Row.Medication;
        return l.Need!.BuyPackages > 0
            ? Texts.Format("Vacation_Buy", c, l.Need.BuyPackages, Texts.Package(m.Type, l.Need.BuyPackages, c), Texts.ShortDate(departure, c))
            : Texts.Get("Vacation_Enough", c);
    }

    public static string BuildShareText(IEnumerable<VacationLine> lines, DateOnly departure, int days, bool withProfile, CultureInfo c)
    {
        var sb = new StringBuilder(Texts.Format("Vacation_ShareTitle", c, Texts.LongDate(departure, c), days));
        foreach (var l in lines.Where(l => l.Need is { NeedUnits: > 0 }))
        {
            var who = withProfile ? $" ({l.Row.Profile.Name})" : "";
            sb.Append('\n').Append($"- {Name(l.Row.Medication)}{who}: {Take(l, c)}; {Buy(l, departure, c)}");
        }
        return sb.ToString();
    }
}
