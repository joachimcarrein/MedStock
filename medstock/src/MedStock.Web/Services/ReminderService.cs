using System.Globalization;
using MedStock.Web.Data;
using MedStock.Web.Domain;
using Microsoft.EntityFrameworkCore;

namespace MedStock.Web.Services;

/// <summary>One intake moment of a profile: profile, day and time. Identifies a reminder (tag and buttons).</summary>
public sealed record ReminderKey(int ProfileId, DateOnly Date, TimeOnly Time)
{
    private const string DateFormat = "yyyyMMdd";
    private const string TimeFormat = "HHmm";

    public string Tag => $"medstock-r-{ProfileId.ToString(CultureInfo.InvariantCulture)}-{Suffix('-')}";
    public string Suffix(char sep) =>
        $"{Date.ToString(DateFormat, CultureInfo.InvariantCulture)}{sep}{Time.ToString(TimeFormat, CultureInfo.InvariantCulture)}";

    /// <summary>Parses "&lt;profile&gt;_&lt;yyyyMMdd&gt;_&lt;HHmm&gt;".</summary>
    public static bool TryParse(ReadOnlySpan<char> s, out ReminderKey key)
    {
        key = null!;
        var parts = s.ToString().Split('_');
        if (parts.Length != 3
            || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var profile) || profile <= 0
            || !DateOnly.TryParseExact(parts[1], DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            || !TimeOnly.TryParseExact(parts[2], TimeFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
            return false;
        key = new ReminderKey(profile, date, time);
        return true;
    }

    public string ActionArgs => $"{ProfileId.ToString(CultureInfo.InvariantCulture)}_{Suffix('_')}";
}

/// <summary>The buttons of an intake reminder: "Taken" (closes it everywhere) and "later" (30 min, 1 h, 2 h).</summary>
public static class ReminderActions
{
    public const string TakenPrefix = "MEDSTOCK_TAKEN_";
    public const string SnoozePrefix = "MEDSTOCK_SNOOZE_";
    public static readonly int[] SnoozeMinutes = [30, 60, 120];

    public static IReadOnlyList<NotifyAction> For(ReminderKey key, CultureInfo c) =>
    [
        new(TakenPrefix + key.ActionArgs, Texts.Get("Reminder_Taken", c)),
        .. SnoozeMinutes.Select(min => new NotifyAction(
            $"{SnoozePrefix}{min.ToString(CultureInfo.InvariantCulture)}_{key.ActionArgs}", Texts.Get($"Reminder_Snooze{min}", c))),
    ];

    public static bool TryParseTaken(string? action, out ReminderKey key)
    {
        key = null!;
        return action is not null && action.StartsWith(TakenPrefix, StringComparison.Ordinal)
               && ReminderKey.TryParse(action.AsSpan(TakenPrefix.Length), out key);
    }

    public static bool TryParseSnooze(string? action, out int minutes, out ReminderKey key)
    {
        minutes = 0;
        key = null!;
        if (action is null || !action.StartsWith(SnoozePrefix, StringComparison.Ordinal)) return false;
        var rest = action[SnoozePrefix.Length..];
        var sep = rest.IndexOf('_');
        return sep > 0 && int.TryParse(rest.AsSpan(0, sep), NumberStyles.None, CultureInfo.InvariantCulture, out minutes)
               && SnoozeMinutes.Contains(minutes) && ReminderKey.TryParse(rest.AsSpan(sep + 1), out key);
    }
}

/// <summary>A medication due at a reminder moment, with the amount to take.</summary>
public sealed record ReminderItem(Medication Medication, decimal Amount);

/// <summary>
/// Intake reminders (separate from the stock alerts): at each intake moment with a time, one notification per user per
/// profile listing the medication due then. Who gets it: users who chose the profile under Settings, or who switched the
/// reminder on for that medication (and not off). Sent once per moment; a moment that was missed (e.g. a restart) is only
/// sent up to <see cref="LateLimit"/> late. Logs contain identifiers only.
/// </summary>
public sealed class ReminderService(MedStockDb db, IHaClient ha, IClock clock, ILogger<ReminderService> log)
{
    public static readonly TimeSpan LateLimit = TimeSpan.FromMinutes(30);

    /// <summary>The medication of a profile due at <paramref name="key"/>'s moment (time-based only; untimed doses get no reminder).</summary>
    public static List<ReminderItem> DueAt(IEnumerable<Medication> meds, ReminderKey key) =>
        meds.Where(m => m.ProfileId == key.ProfileId)
            .Select(m => new ReminderItem(m, AmountAt(m, key.Date, key.Time)))
            .Where(x => x.Amount > 0)
            .OrderBy(x => x.Medication.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

    /// <summary>Units of <paramref name="m"/> due on <paramref name="date"/> at exactly <paramref name="time"/>.</summary>
    public static decimal AmountAt(Medication m, DateOnly date, TimeOnly time)
    {
        if (m.Archived || m.Paused || m.UsageMode == UsageMode.AsNeeded) return 0;
        var usage = MedicationService.UsageOn(m, date);
        if (usage <= 0) return 0;
        return m.UsageMode == UsageMode.Weekly
            ? m.WeeklyTime == time ? usage : 0
            : DoseSchedule.MomentsOf(m).Where(x => x.Time == time).Sum(x => x.Amount);
    }

    /// <summary>All timed moments of today per profile (distinct).</summary>
    public static IEnumerable<ReminderKey> MomentsOn(IEnumerable<Medication> meds, DateOnly date) =>
        meds.Where(m => !m.Archived && !m.Paused && MedicationService.UsageOn(m, date) > 0)
            .SelectMany(m => (m.UsageMode == UsageMode.Weekly
                    ? (m.WeeklyTime is { } t ? [t] : Array.Empty<TimeOnly>())
                    : DoseSchedule.MomentsOf(m).Where(x => x.Time is not null && x.Amount > 0).Select(x => x.Time!.Value))
                .Select(t => new ReminderKey(m.ProfileId, date, t)))
            .Distinct();

    /// <summary>Whether <paramref name="user"/> wants a reminder for <paramref name="m"/>: own exception first, else the profile choice.</summary>
    public static bool Wants(UserSettings user, Medication m, IReadOnlyDictionary<(string, int), bool> overrides) =>
        overrides.TryGetValue((user.UserId, m.Id), out var on) ? on : user.ReminderProfileIds.Contains(m.ProfileId);

    /// <summary>Called every minute: due moments, then due "later" reminders. Never throws for HA problems (retried next tick).</summary>
    public async Task<int> RunAsync(CancellationToken ct = default)
    {
        var now = clock.Now;
        var today = DateOnly.FromDateTime(now);
        var users = await db.UserSettings.Where(u => u.NotifyServices.Count > 0).ToListAsync(ct);
        var snoozes = await db.SnoozedReminders.Where(s => s.DueAt <= now).ToListAsync(ct);
        if (users.Count == 0 && snoozes.Count == 0) return 0;

        var meds = await db.Medications.AsNoTracking().Include(m => m.Profile).Where(m => !m.Archived && !m.Paused).ToListAsync(ct);
        var overrides = (await db.MedicationReminders.AsNoTracking().ToListAsync(ct)).ToDictionary(r => (r.UserId, r.MedicationId), r => r.Enabled);
        int sent = 0;
        string? deepLink = null;
        bool slugLoaded = false;
        async Task<string?> Link()
        {
            if (!slugLoaded) { var slug = await ha.GetSelfSlugAsync(ct); deepLink = slug is null ? null : "/app/" + slug; slugLoaded = true; }
            return deepLink;
        }

        // Moments of today (and of yesterday shortly before midnight) that are due and not too late.
        var candidates = MomentsOn(meds, today).Concat(MomentsOn(meds, today.AddDays(-1)))
            .Where(k => k.Date.ToDateTime(k.Time) is var at && at <= now && now - at < LateLimit)
            .ToList();
        if (candidates.Count > 0)
        {
            var fromDate = today.AddDays(-1);
            var logged = (await db.ReminderLogs.AsNoTracking().Where(l => l.Date >= fromDate).ToListAsync(ct))
                .Select(l => (l.UserId, l.ProfileId, l.Date, l.Time)).ToHashSet();
            foreach (var key in candidates)
            foreach (var user in users)
            {
                if (logged.Contains((user.UserId, key.ProfileId, key.Date, key.Time))) continue;
                var items = DueAt(meds, key).Where(x => Wants(user, x.Medication, overrides)).ToList();
                if (items.Count == 0) continue;
                var outcome = await SendAsync(user, key, items, await Link(), ct);
                if (outcome == NotifyOutcome.TransportError) continue; // retried next minute (until too late)
                db.ReminderLogs.Add(new ReminderLog { UserId = user.UserId, ProfileId = key.ProfileId, Date = key.Date, Time = key.Time, SentAt = DateTime.UtcNow });
                if (outcome == NotifyOutcome.Ok) sent++;
                log.LogInformation("Reminder for profile {ProfileId} at {Time} to user {UserId}: {Outcome}", key.ProfileId, key.Time, user.UserId, outcome);
            }
        }

        // "Later" reminders that are due now.
        if (snoozes.Count > 0)
        {
            var all = await db.UserSettings.ToListAsync(ct);
            foreach (var s in snoozes)
            {
                var user = all.FirstOrDefault(u => u.UserId == s.UserId);
                var key = new ReminderKey(s.ProfileId, s.Date, s.Time);
                var items = user is null ? [] : DueAt(meds, key);
                if (items.Count > 0)
                {
                    var outcome = await SendAsync(user!, key, items, await Link(), ct);
                    if (outcome == NotifyOutcome.TransportError && now - s.DueAt < LateLimit) continue; // retry next minute
                    if (outcome == NotifyOutcome.Ok) sent++;
                    log.LogInformation("Snoozed reminder for profile {ProfileId} to user {UserId}: {Outcome}", s.ProfileId, s.UserId, outcome);
                }
                db.SnoozedReminders.Remove(s);
            }
        }

        // Housekeeping: reminder logs older than two days.
        var purgeBefore = today.AddDays(-2);
        db.ReminderLogs.RemoveRange(db.ReminderLogs.Where(l => l.Date < purgeBefore));
        await db.SaveChangesAsync(ct);
        return sent;
    }

    /// <summary>Sends one reminder to all devices of a user. Ok when at least one device got it.</summary>
    private async Task<NotifyOutcome> SendAsync(UserSettings user, ReminderKey key, List<ReminderItem> items, string? deepLink, CancellationToken ct)
    {
        var c = NotificationService.CultureFor(user);
        var profile = items[0].Medication.Profile?.Name ?? "";
        var time = key.Time.ToString("HH:mm", CultureInfo.InvariantCulture);
        var body = user.DiscreetNotifications
            ? $"{time} {profile}"
            : $"{time} {profile}: " + string.Join(", ", items.Select(x =>
            {
                var m = x.Medication;
                var name = string.IsNullOrWhiteSpace(m.Strength) ? m.Name : $"{m.Name} {m.Strength}";
                return $"{name} ({x.Amount.ToString("G29", c)} {Texts.Unit(m.Type, x.Amount, c)})";
            }));
        bool anyOk = false, transport = false;
        foreach (var service in user.NotifyServices)
        {
            var outcome = await ha.NotifyAsync(service, Texts.Get("Reminder_Title", c), body, deepLink, key.Tag, ReminderActions.For(key, c), ct);
            if (outcome == NotifyOutcome.Ok) anyOk = true;
            else if (outcome == NotifyOutcome.TransportError) transport = true;
        }
        return anyOk ? NotifyOutcome.Ok : transport ? NotifyOutcome.TransportError : NotifyOutcome.UnknownService;
    }

    /// <summary>"Taken": removes the reminder from every device it went to, and cancels pending "later" reminders for it.</summary>
    public async Task TakenAsync(ReminderKey key, CancellationToken ct = default)
    {
        var userIds = await db.ReminderLogs.Where(l => l.ProfileId == key.ProfileId && l.Date == key.Date && l.Time == key.Time)
            .Select(l => l.UserId).ToListAsync(ct);
        var snoozed = await db.SnoozedReminders.Where(s => s.ProfileId == key.ProfileId && s.Date == key.Date && s.Time == key.Time).ToListAsync(ct);
        userIds.AddRange(snoozed.Select(s => s.UserId));
        db.SnoozedReminders.RemoveRange(snoozed);
        await db.SaveChangesAsync(ct);
        var ids = userIds.Distinct().ToList();
        var users = await db.UserSettings.AsNoTracking().Where(u => ids.Contains(u.UserId)).ToListAsync(ct);
        foreach (var service in users.SelectMany(u => u.NotifyServices).Distinct())
            await ha.ClearNotificationAsync(service, key.Tag, ct);
        log.LogInformation("Reminder for profile {ProfileId} at {Time} marked taken; cleared on {Users} users' devices", key.ProfileId, key.Time, users.Count);
    }

    /// <summary>"Later": the same reminder again for this user after <paramref name="minutes"/>.</summary>
    public async Task SnoozeAsync(ReminderKey key, int minutes, string? haUserId, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(haUserId) || !await db.UserSettings.AnyAsync(u => u.UserId == haUserId, ct))
        {
            log.LogInformation("Snooze for profile {ProfileId} ignored: unknown user", key.ProfileId);
            return;
        }
        var existing = await db.SnoozedReminders.Where(s => s.UserId == haUserId && s.ProfileId == key.ProfileId && s.Date == key.Date && s.Time == key.Time).ToListAsync(ct);
        db.SnoozedReminders.RemoveRange(existing);
        db.SnoozedReminders.Add(new SnoozedReminder
        {
            UserId = haUserId, ProfileId = key.ProfileId, Date = key.Date, Time = key.Time, DueAt = clock.Now.AddMinutes(minutes),
        });
        await db.SaveChangesAsync(ct);
        log.LogInformation("Reminder for profile {ProfileId} at {Time} snoozed {Minutes} min by user {UserId}", key.ProfileId, key.Time, minutes, haUserId);
    }
}
