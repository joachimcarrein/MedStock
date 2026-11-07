using MedStock.Web.Domain;

namespace MedStock.Web.Services;

public abstract record NotificationDecision
{
    public sealed record Nothing : NotificationDecision;
    /// <summary>No level active anymore: delete all log rows of the medication (re-arm).</summary>
    public sealed record ClearLog : NotificationDecision;
    /// <summary>Send one message for <see cref="Level"/>, then log all <see cref="ActiveLevels"/>.</summary>
    public sealed record Send(int Level, IReadOnlyList<int> ActiveLevels) : NotificationDecision;
}

public static class NotificationRules
{
    public const int MinStockLevel = -1;

    public static IReadOnlyList<int> Levels(int alertDays)
    {
        alertDays = Math.Max(0, alertDays);
        return new[] { alertDays, alertDays / 2, alertDays / 4, 0 }.Distinct().ToList();
    }

    /// <summary>Whether a medication takes part in notifications at all.</summary>
    public static bool IsEligible(Medication m, StockInput input) =>
        !m.Archived && !m.Paused && m.NotificationsEnabled &&
        (m.UsageMode == UsageMode.AsNeeded || StockCalculator.HasRegularUsage(input));

    public static IReadOnlyList<int> ActiveLevels(UsageMode mode, StockResult r, decimal minStockUnits, int alertDays)
    {
        if (mode == UsageMode.AsNeeded)
            return r.Remaining <= minStockUnits ? [MinStockLevel] : [];
        if (r.CapReached) return [];
        int days = r.State == StockState.Empty ? 0 : r.DaysRemaining ?? int.MaxValue;
        return Levels(alertDays).Where(l => days <= l).ToList();
    }

    public static NotificationDecision Decide(IReadOnlyList<int> active, IReadOnlyCollection<int> logged)
    {
        if (active.Count == 0)
            return logged.Count > 0 ? new NotificationDecision.ClearLog() : new NotificationDecision.Nothing();
        var pending = active.Where(l => !logged.Contains(l)).ToList();
        if (pending.Count == 0) return new NotificationDecision.Nothing();
        return new NotificationDecision.Send(pending.Min(), active);
    }

    /// <summary>The users (and their devices) that should receive a message about a medication of <paramref name="profileId"/>.</summary>
    public static IEnumerable<(string UserId, string Service)> Recipients(IEnumerable<UserSettings> users, int profileId) =>
        from u in users
        where u.NotificationsEnabled
        where u.NotifyProfileIds.Count == 0 || u.NotifyProfileIds.Contains(profileId)
        from s in u.NotifyServices.Distinct()
        select (u.UserId, s);
}

public static class NotificationScheduler
{
    public static DateTime DueAt(DateTime now, TimeOnly time) => DateOnly.FromDateTime(now).ToDateTime(time);

    public static bool ShouldRun(DateTime now, TimeOnly notificationTime, DateTime? lastRunAt)
    {
        var due = DueAt(now, notificationTime);
        return now >= due && (lastRunAt is null || lastRunAt < due);
    }
}
