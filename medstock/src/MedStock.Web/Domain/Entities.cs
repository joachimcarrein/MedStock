namespace MedStock.Web.Domain;

public enum MedicationType { Pill, Capsule, NasalSpray, Inhaler, Drops, Injection, Sachet, Liquid, Other }

public enum UsageMode { PerDay, Weekly, AsNeeded }

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
    public decimal PerDay { get; set; }
    /// <summary>Usage per weekday, index 0 = Monday. Always 7 values.</summary>
    public decimal[] Weekly { get; set; } = new decimal[7];
    public decimal MinStockUnits { get; set; }
    public int? AlertDays { get; set; }
    public int? CoverageDays { get; set; }
    public bool NotificationsEnabled { get; set; } = true;
    public string? Barcode { get; set; }
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
    public DateTime? LastRunAt { get; set; }
    public string WrittenByVersion { get; set; } = "";
    /// <summary>Opt-in: publish a "days remaining" sensor per medication to Home Assistant via MQTT discovery.</summary>
    public bool SensorsEnabled { get; set; }
    /// <summary>Medication ids that currently have a retained discovery config (comma separated), so removed ones get cleaned up.</summary>
    public string? PublishedSensorIds { get; set; }
    /// <summary>Optional todo.* entity: medication that runs low is added to this list (once while it is open).</summary>
    public string? TodoEntityId { get; set; }
}
