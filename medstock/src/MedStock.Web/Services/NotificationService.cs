using System.Globalization;
using MedStock.Web.Data;
using MedStock.Web.Domain;
using Microsoft.EntityFrameworkCore;

namespace MedStock.Web.Services;

/// <summary>Result of one notification run. <see cref="Lines"/> are shown in Settings (UI only, never logged).</summary>
public sealed record RunReport(int Checked, int Messages, int Deliveries, bool TransportError, IReadOnlyList<string> Lines);

/// <summary>Process-wide state of the scheduler: failure streak, warning throttle, once-a-day logging of unknown devices.</summary>
public sealed class NotificationState
{
    public int ConsecutiveFailures { get; set; }
    public DateTime LastWarningAt { get; set; } = DateTime.MinValue;
    public HashSet<(DateOnly, string)> LoggedUnknownServices { get; } = [];
    public SemaphoreSlim RunLock { get; } = new(1, 1);
}

/// <summary>
/// The daily check (§3.8). For each eligible medication: decide on the hysteresis levels, send one message per device in the
/// recipient's language, and log the active levels once at least one delivery succeeded. Logs only contain identifiers.
/// </summary>
public sealed class NotificationService(MedStockDb db, IHaClient ha, IClock clock, NotificationState state, ILogger<NotificationService> log)
{
    public const int MaxFailedTicks = 30;
    /// <summary>NotificationLog level meaning "put on the to-do list in this cycle" (never an alert level).</summary>
    public const int TodoMarkerLevel = -100;
    public static readonly TimeSpan WarningInterval = TimeSpan.FromMinutes(15);

    /// <summary>Called every minute by the worker: runs when due, and handles transport failures (retry next tick).</summary>
    public async Task<RunReport?> RunIfDueAsync(CancellationToken ct = default)
    {
        var global = await db.GlobalSettings.SingleAsync(ct);
        var now = clock.Now;
        if (!NotificationScheduler.ShouldRun(now, global.NotificationTime, global.LastRunAt)) return null;

        var report = await RunAsync(ct);
        if (!report.TransportError)
        {
            state.ConsecutiveFailures = 0;
            global.LastRunAt = now;
        }
        else if (++state.ConsecutiveFailures >= MaxFailedTicks)
        {
            log.LogError("Daily notification run gave up after {Attempts} attempts; Home Assistant unreachable", state.ConsecutiveFailures);
            state.ConsecutiveFailures = 0;
            global.LastRunAt = now;
        }
        else if (now - state.LastWarningAt >= WarningInterval)
        {
            log.LogWarning("Daily notification run could not reach Home Assistant (attempt {Attempt}); retrying every minute", state.ConsecutiveFailures);
            state.LastWarningAt = now;
        }
        await db.SaveChangesAsync(ct);
        return report;
    }

    /// <summary>The actual check, also used by "run daily check now". Does not touch LastRunAt.</summary>
    public async Task<RunReport> RunAsync(CancellationToken ct = default)
    {
        await state.RunLock.WaitAsync(ct);
        try { return await RunCoreAsync(ct); }
        finally { state.RunLock.Release(); }
    }

    private async Task<RunReport> RunCoreAsync(CancellationToken ct)
    {
        var today = clock.Today;
        var global = await db.GlobalSettings.AsNoTracking().SingleAsync(ct);
        var rows = await MedicationQueries.LoadAsync(db, today);
        var users = await db.UserSettings.ToListAsync(ct);
        var logs = (await db.NotificationLogs.ToListAsync(ct)).ToLookup(l => l.MedicationId);
        var lines = new List<string>();
        int checkedCount = 0, messages = 0, deliveries = 0;
        bool transportError = false;
        string? deepLink = null;
        bool slugLoaded = false;

        foreach (var row in rows)
        {
            var m = row.Medication;
            if (!NotificationRules.IsEligible(m, StockInput.From(m, row.Snapshot))) continue;
            checkedCount++;
            var alertDays = m.AlertDays ?? global.DefaultAlertDays;
            var active = NotificationRules.ActiveLevels(m.UsageMode, row.Stock, m.MinStockUnits, alertDays);
            var logged = logs[m.Id].Select(l => l.Level).ToList();
            var decision = NotificationRules.Decide(active, logged);

            // To-do list: every alert, and once per cycle for medication that is already low (e.g. when the list was
            // chosen after its alert was sent). Skipped when the item is still open on the list; the marker row is
            // removed with the other log rows when the stock is sufficient again, or when another list is chosen.
            if (!string.IsNullOrEmpty(global.TodoEntityId) && active.Count > 0
                && (decision is NotificationDecision.Send || !logged.Contains(TodoMarkerLevel)))
            {
                var item = string.IsNullOrWhiteSpace(m.Strength) ? $"{m.Name} ({row.Profile.Name})" : $"{m.Name} {m.Strength} ({row.Profile.Name})";
                var added = await ha.AddTodoItemIfMissingAsync(global.TodoEntityId, item, ct);
                log.LogInformation("Medication {MedicationId}: to-do item {Result}", m.Id, added ? "ensured" : "failed");
                if (added && !logged.Contains(TodoMarkerLevel))
                {
                    db.NotificationLogs.Add(new NotificationLog { MedicationId = m.Id, Level = TodoMarkerLevel, SentAt = DateTime.UtcNow });
                    logged.Add(TodoMarkerLevel);
                }
                if (decision is not NotificationDecision.Send) lines.Add($"{m.Name} ({row.Profile.Name}): added to the to-do list");
            }

            switch (decision)
            {
                case NotificationDecision.ClearLog:
                    db.NotificationLogs.RemoveRange(logs[m.Id]);
                    lines.Add($"{m.Name} ({row.Profile.Name}): re-armed");
                    log.LogInformation("Medication {MedicationId}: notification levels re-armed", m.Id);
                    break;

                case NotificationDecision.Send send:
                    var recipients = NotificationRules.Recipients(users, m.ProfileId).ToList();
                    if (recipients.Count == 0)
                    {
                        lines.Add($"{m.Name} ({row.Profile.Name}): {LevelText(send.Level)}, no recipients (no one has saved notification settings for this profile)");
                        log.LogInformation("Medication {MedicationId}: level {Level} due but no recipients", m.Id, send.Level);
                        break;
                    }
                    if (!slugLoaded)
                    {
                        var slug = await ha.GetSelfSlugAsync(ct);
                        deepLink = slug is null ? null : "/app/" + slug;
                        slugLoaded = true;
                    }
                    messages++;
                    int ok = 0;
                    foreach (var (userId, service) in recipients)
                    {
                        var user = users.First(u => u.UserId == userId);
                        var culture = CultureFor(user);
                        var text = user.DiscreetNotifications
                            ? Texts.DiscreetMessage(row.Profile.Name, m.UsageMode, row.Stock, culture)
                            : Texts.NotificationMessage(m.Name, row.Profile.Name, m.Type, m.UsageMode, row.Stock, culture);
                        var outcome = await ha.NotifyAsync(service, Texts.Get("Notify_Title", culture), text, deepLink, $"medstock-{m.Id}",
                            [NotificationActions.Purchase(m, culture)], ct);
                        switch (outcome)
                        {
                            case NotifyOutcome.Ok:
                                ok++;
                                user.StaleNotifyServices.Remove(service);
                                break;
                            case NotifyOutcome.UnknownService:
                                if (!user.StaleNotifyServices.Contains(service)) user.StaleNotifyServices.Add(service);
                                if (state.LoggedUnknownServices.Add((today, service)))
                                    log.LogWarning("Notify service {Service} not found in Home Assistant; skipped and marked in Settings", service);
                                lines.Add($"{service}: device not found");
                                break;
                            default:
                                transportError = true;
                                break;
                        }
                    }
                    deliveries += ok;
                    if (ok > 0)
                    {
                        foreach (var level in send.ActiveLevels.Where(l => !logged.Contains(l)))
                            db.NotificationLogs.Add(new NotificationLog { MedicationId = m.Id, Level = level, SentAt = DateTime.UtcNow });
                    }
                    lines.Add($"{m.Name} ({row.Profile.Name}): {LevelText(send.Level)}, {ok}/{recipients.Count} delivered");
                    log.LogInformation("Medication {MedicationId}: level {Level} sent to {Delivered}/{Devices} devices",
                        m.Id, send.Level, ok, recipients.Count);
                    break;
            }
        }

        // StaleNotifyServices is an EF primitive collection: in-place changes are detected.
        await db.SaveChangesAsync(ct);
        log.LogInformation("Notification run: {Checked} checked, {Messages} messages, {Deliveries} deliveries, transport error: {TransportError}",
            checkedCount, messages, deliveries, transportError);
        if (lines.Count == 0) lines.Add("Nothing to send.");
        return new RunReport(checkedCount, messages, deliveries, transportError, lines);
    }

    private static string LevelText(int level) => level == NotificationRules.MinStockLevel ? "under minimum stock" : $"{level}-day level";

    /// <summary>The recipient's language: the explicit setting, else the culture of their last visit, else English.</summary>
    public static CultureInfo CultureFor(UserSettings u) =>
        new(u.Language ?? u.LastCulture ?? "en");
}

/// <summary>Checks every minute whether the daily run is due (§3.8). Never throws; never calls HA during host startup.</summary>
public sealed class NotificationWorker(IServiceScopeFactory scopes, ILogger<NotificationWorker> log) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(Interval);
        while (await timer.WaitForNextTickAsync(ct))
        {
            try
            {
                using var scope = scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<NotificationService>().RunIfDueAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                log.LogError("Notification tick failed: {Error}", ex.GetType().Name);
            }
        }
    }
}
