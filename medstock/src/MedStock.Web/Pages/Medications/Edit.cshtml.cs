using MedStock.Web.Data;
using MedStock.Web.Domain;
using MedStock.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;

namespace MedStock.Web.Pages.Medications;

/// <summary>One form for new and edit (§3.5).</summary>
public class EditModel(MedStockDb db, MedicationService service, IClock clock, IStringLocalizer<SharedResource> l) : PageModel
{
    public const string NewProfileValue = "new";

    [BindProperty] public MedicationInput Input { get; set; } = new();
    public List<Profile> Profiles { get; private set; } = [];
    public GlobalSettings Global { get; private set; } = new();
    public bool IsNew => Input.Id == 0;
    public string? Conflict { get; private set; }

    public async Task<IActionResult> OnGetAsync(int? id, int? profileId, string? barcode)
    {
        await LoadAsync();
        if (id is null)
        {
            if (Profiles.Count == 0) return RedirectToPage("/Settings/Profiles", new { next = "new" });
            Input = new MedicationInput
            {
                Profile = (Profiles.FirstOrDefault(p => p.Id == profileId) ?? Profiles[0]).Id.ToString(),
                TakenToday = MedicationService.DefaultTakenToday(clock.Now),
                Barcode = Barcodes.Normalize(barcode),
            };
            return Page();
        }

        var m = await db.Medications.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id && !x.Archived);
        if (m is null) return NotFound();
        Input = MedicationInput.From(m, (await service.LatestSnapshotAsync(m.Id))?.Id ?? 0);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        await LoadAsync();
        Validate();
        if (!ModelState.IsValid) return Page();

        // A new profile is attached through the navigation property so it is saved in the same SaveChanges as the
        // medication (one transaction, and no database lock held while waiting for the service lock).
        Profile? newProfile = null;
        if (Input.Profile == NewProfileValue)
            newProfile = new Profile
            {
                Name = Input.NewProfileName!.Trim(),
                Color = Settings.ProfilesModel.Palette[Profiles.Count % Settings.ProfilesModel.Palette.Length],
                SortOrder = Profiles.Count == 0 ? 0 : Profiles.Max(x => x.SortOrder) + 1,
            };
        void SetProfile(Medication m)
        {
            if (newProfile is null) m.ProfileId = int.Parse(Input.Profile!);
            else m.Profile = newProfile;
        }

        int id;
        if (IsNew)
        {
            var m = new Medication();
            SetProfile(m);
            Input.ApplyTo(m);
            m.Barcode = Barcodes.Normalize(Input.Barcode);
            id = await service.CreateAsync(m, Input.OpenUnits ?? 0, Input.SealedPackages ?? 0, Input.TakenToday);
        }
        else
        {
            id = Input.Id;
            try
            {
                await service.UpdateAsync(id, Input.ExpectedSnapshotId, m =>
                {
                    SetProfile(m);
                    Input.ApplyTo(m);
                });
            }
            catch (KeyNotFoundException) { return NotFound(); }
            catch (StaleSnapshotException)
            {
                Conflict = l["Conflict"];
                Input.ExpectedSnapshotId = (await service.LatestSnapshotAsync(id))?.Id ?? 0;
                ModelState.Remove($"{nameof(Input)}.{nameof(Input.ExpectedSnapshotId)}");
                Response.StatusCode = StatusCodes.Status409Conflict;
                return Page();
            }
        }
        return RedirectToPage("/Medications/Details", new { id });
    }

    private void Validate()
    {
        void Err(string field, string key, params object[] args) =>
            ModelState.AddModelError($"{nameof(Input)}.{field}", l[key, args]);

        if (Input.Profile == NewProfileValue)
        {
            var name = Input.NewProfileName?.Trim();
            if (string.IsNullOrEmpty(name)) Err(nameof(Input.NewProfileName), "Val_Required");
            else if (name.Length > 50) Err(nameof(Input.NewProfileName), "Val_MaxLength", 50);
            else if (Profiles.Any(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)))
                Err(nameof(Input.NewProfileName), "Profile_NameExists");
        }
        else if (!int.TryParse(Input.Profile, out var pid) || Profiles.All(p => p.Id != pid))
            Err(nameof(Input.Profile), "Val_Required");

        var nameValue = Input.Name?.Trim();
        if (string.IsNullOrEmpty(nameValue)) Err(nameof(Input.Name), "Val_Required");
        else if (nameValue.Length > 100) Err(nameof(Input.Name), "Val_MaxLength", 100);
        if (Input.Strength?.Trim().Length > 50) Err(nameof(Input.Strength), "Val_MaxLength", 50);
        if (Input.UnitsPerPackage is not > 0) Err(nameof(Input.UnitsPerPackage), "Val_Positive");
        if (Input.PerDay < 0) Err(nameof(Input.PerDay), "Val_NonNegative");
        if (Input.Weekly.Length != 7) Input.Weekly = [.. Input.Weekly.Concat(new decimal[7]).Take(7)];
        for (var i = 0; i < 7; i++)
            if (Input.Weekly[i] < 0) Err($"{nameof(Input.Weekly)}[{i}]", "Val_NonNegative");
        if (Input.TakeAlongUnits is <= 0) Err(nameof(Input.TakeAlongUnits), "Val_Positive");
        else if (Input.TakeAlongUnits > Input.UnitsPerPackage) Err(nameof(Input.TakeAlongUnits), "Val_TakeAlongMax");
        if (Input.MinStockUnits < 0) Err(nameof(Input.MinStockUnits), "Val_NonNegative");
        if (Input.AlertDays is < 0 or > 3650) Err(nameof(Input.AlertDays), "Val_Range");
        if (Input.CoverageDays is < 0 or > 3650) Err(nameof(Input.CoverageDays), "Val_Range");
        if (IsNew)
        {
            if (Input.OpenUnits < 0) Err(nameof(Input.OpenUnits), "Val_NonNegative");
            if (Input.SealedPackages < 0) Err(nameof(Input.SealedPackages), "Val_NonNegative");
        }
    }

    private async Task LoadAsync()
    {
        Profiles = await db.Profiles.AsNoTracking().OrderBy(p => p.SortOrder).ThenBy(p => p.Id).ToListAsync();
        Global = await db.GlobalSettings.AsNoTracking().SingleAsync();
    }
}

public sealed class MedicationInput
{
    public int Id { get; set; }
    public int ExpectedSnapshotId { get; set; }
    /// <summary>Profile id, or <see cref="EditModel.NewProfileValue"/> to create one in the same POST.</summary>
    public string? Profile { get; set; }
    public string? NewProfileName { get; set; }
    public string? Name { get; set; }
    public string? Strength { get; set; }
    public MedicationType Type { get; set; } = MedicationType.Pill;
    public decimal? UnitsPerPackage { get; set; }
    public decimal? TakeAlongUnits { get; set; }
    /// <summary>Only for a new medication created from a scan of an unknown code.</summary>
    public string? Barcode { get; set; }
    public UsageMode UsageMode { get; set; } = UsageMode.PerDay;
    public decimal PerDay { get; set; }
    /// <summary>Index 0 = Monday, regardless of the UI order.</summary>
    public decimal[] Weekly { get; set; } = new decimal[7];
    public decimal MinStockUnits { get; set; }
    public int? AlertDays { get; set; }
    public int? CoverageDays { get; set; }
    public bool NotificationsEnabled { get; set; } = true;
    public decimal? OpenUnits { get; set; }
    public int? SealedPackages { get; set; }
    public bool TakenToday { get; set; }

    public static MedicationInput From(Medication m, int latestSnapshotId) => new()
    {
        Id = m.Id, ExpectedSnapshotId = latestSnapshotId, Profile = m.ProfileId.ToString(), Name = m.Name,
        Strength = m.Strength, Type = m.Type, UnitsPerPackage = m.UnitsPerPackage, TakeAlongUnits = m.TakeAlongUnits, UsageMode = m.UsageMode,
        PerDay = m.PerDay, Weekly = (decimal[])m.Weekly.Clone(), MinStockUnits = m.MinStockUnits, AlertDays = m.AlertDays,
        CoverageDays = m.CoverageDays, NotificationsEnabled = m.NotificationsEnabled,
    };

    public void ApplyTo(Medication m)
    {
        m.Name = Name!.Trim();
        m.Strength = string.IsNullOrWhiteSpace(Strength) ? null : Strength.Trim();
        m.Type = Type;
        m.UnitsPerPackage = UnitsPerPackage!.Value;
        m.TakeAlongUnits = TakeAlongUnits;
        m.UsageMode = UsageMode;
        // Only the active mode's values are kept, so switching modes back and forth leaves no stale usage behind.
        m.PerDay = UsageMode == UsageMode.PerDay ? PerDay : 0;
        m.Weekly = UsageMode == UsageMode.Weekly ? (decimal[])Weekly.Clone() : new decimal[7];
        m.MinStockUnits = MinStockUnits;
        m.AlertDays = AlertDays;
        m.CoverageDays = CoverageDays;
        m.NotificationsEnabled = NotificationsEnabled;
    }
}
