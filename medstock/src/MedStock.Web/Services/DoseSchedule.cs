using MedStock.Web.Domain;

namespace MedStock.Web.Services;

/// <summary>
/// The intake moments of a medication and which of today's doses are still to come. A count is "stock now"; a snapshot is
/// "stock at the end of the day", so a recount subtracts the doses that still follow today (by their time; a dose without a
/// time counts as still to come until <see cref="GlobalSettings.UntimedTakenFrom"/>).
/// </summary>
public static class DoseSchedule
{
    /// <summary>The moments of a dose day in PerDay mode (an old medication without moments: one untimed moment).</summary>
    public static IReadOnlyList<DoseMoment> MomentsOf(Medication m) =>
        m.Moments.Count > 0 ? m.Moments : m.PerDay > 0 ? [new DoseMoment(null, m.PerDay)] : [];

    /// <summary>Today's doses that are still to come at <paramref name="now"/> (empty when today is no dose day).</summary>
    public static List<DoseMoment> PendingToday(Medication m, DateTime now, TimeOnly untimedFrom)
    {
        var today = DateOnly.FromDateTime(now);
        var time = TimeOnly.FromDateTime(now);
        var usage = MedicationService.UsageOn(m, today);
        if (usage <= 0) return [];
        bool Pending(TimeOnly? t) => time < (t ?? untimedFrom);
        if (m.UsageMode == UsageMode.Weekly)
            return Pending(m.WeeklyTime) ? [new DoseMoment(m.WeeklyTime, usage)] : [];
        return MomentsOf(m).Where(x => x.Amount > 0 && Pending(x.Time)).OrderBy(x => x.Time ?? untimedFrom).ToList();
    }

    /// <summary>Whether a dose of this medication has no time (then the untimed rule applies).</summary>
    public static bool HasUntimed(Medication m) => m.UsageMode switch
    {
        UsageMode.PerDay => MomentsOf(m).Any(x => x.Time is null),
        UsageMode.Weekly => m.WeeklyTime is null,
        _ => false,
    };
}
