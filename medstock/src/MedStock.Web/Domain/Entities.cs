namespace MedStock.Web.Domain;

public enum MedicationType { Pill, Capsule, NasalSpray, Inhaler, Drops, Injection, Sachet, Liquid, Other }

public enum UsageMode { PerDay, Weekly, AsNeeded }

/// <summary>One intake moment of a daily schedule: an optional time of day and the amount taken then.</summary>
public sealed record DoseMoment(TimeOnly? Time, decimal Amount);

public enum SnapshotReason { Initial, Purchase, Recount, DoseChange, Pause, Resume, Intake }

public class User
{
    public string Id { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public DateTime FirstSeen { get; set; }
    public DateTime LastSeen { get; set; }
    public UserSettings? Settings { get; set; }
}

public class UserSettings
{
    public string UserId { get; set; } = "";
    public bool NotificationsEnabled { get; set; } = true;
    public List<string> NotifyServices { get; set; } = [];
    /// <summary>Empty = all profiles.</summary>
    public List<int> NotifyProfileIds { get; set; } = [];
    /// <summary>null = automatic (Accept-Language).</summary>
    public string? Language { get; set; }
    /// <summary>null = all profiles.</summary>
    public int? LastProfileFilter { get; set; }
    public List<string> StaleNotifyServices { get; set; } = [];
    /// <summary>True once the default device selection has been applied (first visit of Settings).</summary>
    public bool DevicesInitialized { get; set; }
    /// <summary>UI culture of the user's last request ("nl"/"en"): the language of notifications when <see cref="Language"/> is automatic.</summary>
    public string? LastCulture { get; set; }
    /// <summary>Notifications say "a medication of &lt;profile&gt;" instead of its name (lock screens, shared devices).</summary>
    public bool DiscreetNotifications { get; set; }
    /// <summary>Profiles for which this user gets intake reminders (none by default; separate from stock alerts).</summary>
    public List<int> ReminderProfileIds { get; set; } = [];
}

/// <summary>A user's own exception for one medication: reminders always (true) or never (false), whatever the profile choice.</summary>
public class MedicationReminder
{
    public string UserId { get; set; } = "";
    public int MedicationId { get; set; }
    public bool Enabled { get; set; }
}

/// <summary>An intake reminder already handled for a user: profile, day and moment (once per moment).</summary>
public class ReminderLog
{
    public int Id { get; set; }
    public string UserId { get; set; } = "";
    public int ProfileId { get; set; }
    public DateOnly Date { get; set; }
    public TimeOnly Time { get; set; }
    public DateTime SentAt { get; set; }
}

/// <summary>"Remind me later": the same reminder again for this user at <see cref="DueAt"/> (local time).</summary>
public class SnoozedReminder
{
    public int Id { get; set; }
    public string UserId { get; set; } = "";
    public int ProfileId { get; set; }
    public DateOnly Date { get; set; }
    public TimeOnly Time { get; set; }
    public DateTime DueAt { get; set; }
}

public class Profile
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Color { get; set; } = "#1e88e5";
    public int SortOrder { get; set; }
    public int? CoverageDays { get; set; }
    public List<Medication> Medications { get; set; } = [];
}

public class Medication
{
    public int Id { get; set; }
    public int ProfileId { get; set; }
    public Profile? Profile { get; set; }
    public string Name { get; set; } = "";
    public string? Strength { get; set; }
    public MedicationType Type { get; set; }
    public decimal UnitsPerPackage { get; set; } = 1;
    public UsageMode UsageMode { get; set; }
    /// <summary>Usage on a dose day (PerDay mode): the sum of <see cref="Moments"/>.</summary>
    public decimal PerDay { get; set; }
    /// <summary>
    /// Intake moments of a day (PerDay mode). Empty = one moment of <see cref="PerDay"/> without a time (medication saved
    /// before moments existed).
    /// </summary>
    public List<DoseMoment> Moments { get; set; } = [];
    /// <summary>Usage per weekday, index 0 = Monday. Always 7 values.</summary>
    public decimal[] Weekly { get; set; } = new decimal[7];
    /// <summary>Time of the weekly doses; null = no time.</summary>
    public TimeOnly? WeeklyTime { get; set; }
    /// <summary>Every N days (PerDay) or every N weeks (Weekly); 1 = every day / every week.</summary>
    public int Every { get; set; } = 1;
    /// <summary>A day of the rhythm when <see cref="Every"/> &gt; 1: a dose day (PerDay) or a day in a dose week (Weekly).</summary>
    public DateOnly? EveryFrom { get; set; }
    public decimal MinStockUnits { get; set; }
    public int? AlertDays { get; set; }
    public int? CoverageDays { get; set; }
    public bool NotificationsEnabled { get; set; } = true;
    public string? Barcode { get; set; }
    /// <summary>Smallest amount to take along on a trip (e.g. a strip of 10). Null = the default for the type.</summary>
    public decimal? TakeAlongUnits { get; set; }
    /// <summary>Last day of a course (e.g. antibiotics); no use after it. Null = ongoing.</summary>
    public DateOnly? EndDate { get; set; }
    /// <summary>Available without a prescription: left off the prescription for the doctor (bought yourself).</summary>
    public bool OverTheCounter { get; set; }
    public bool HasPhoto { get; set; }
    public bool Paused { get; set; }
    public bool Archived { get; set; }
    public DateTime CreatedAt { get; set; }
    public List<Snapshot> Snapshots { get; set; } = [];
}

public class Snapshot
{
    public int Id { get; set; }
    public int MedicationId { get; set; }
    public DateOnly Date { get; set; }
    public decimal TotalUnits { get; set; }
    public SnapshotReason Reason { get; set; }
    public DateTime CreatedAt { get; set; }
    public string? CreatedByUserId { get; set; }
}

public class NotificationLog
{
    public int Id { get; set; }
    public int MedicationId { get; set; }
    /// <summary>Days level; -1 = minimum stock (as needed).</summary>
    public int Level { get; set; }
    public DateTime SentAt { get; set; }
}

public class GlobalSettings
{
    public int Id { get; set; } = 1;
    public int CoverageDays { get; set; } = 90;
    public int DefaultAlertDays { get; set; } = 14;
    public TimeOnly NotificationTime { get; set; } = new(9, 0);
    /// <summary>A dose without a time counts as taken from this time of day (recount and initial stock).</summary>
    public TimeOnly UntimedTakenFrom { get; set; } = new(20, 0);
    public DateTime? LastRunAt { get; set; }
    public string WrittenByVersion { get; set; } = "";
    /// <summary>Opt-in: publish a "days remaining" sensor per medication to Home Assistant via MQTT discovery.</summary>
    public bool SensorsEnabled { get; set; }
    /// <summary>
    /// Sensors that currently have a retained MQTT discovery config (comma separated: "12" = medication 12, "p3" = summary of
    /// profile 3), so removed ones get cleaned up.
    /// </summary>
    public string? PublishedSensorIds { get; set; }
    /// <summary>Opt-in: one sensor per profile with the lowest days remaining and counts, without medication names.</summary>
    public bool SummarySensorsEnabled { get; set; }
    /// <summary>Opt-in: one sensor per profile with the next intake moment (a timestamp), for automations.</summary>
    public bool NextIntakeSensorsEnabled { get; set; }
    /// <summary>Entity ids written through the REST states API (no MQTT broker), comma separated, so removed ones get deleted.</summary>
    public string? PublishedStateEntities { get; set; }
    /// <summary>Optional todo.* entity: medication that runs low is added to this list (once while it is open).</summary>
    public string? TodoEntityId { get; set; }
}
