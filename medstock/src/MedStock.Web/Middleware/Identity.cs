using MedStock.Web.Data;
using MedStock.Web.Domain;
using MedStock.Web.Services;
using Microsoft.EntityFrameworkCore;

namespace MedStock.Web.Middleware;

/// <summary>The Home Assistant user of the current request.</summary>
public sealed class CurrentUser
{
    public const string HouseholdId = "household";
    public const string DevId = "dev";
    public const string ChooseCookie = "medstock_user";

    public string Id { get; set; } = HouseholdId;
    public string DisplayName { get; set; } = "Household";
    /// <summary>No HA user header and no explicit choice: shared fallback user (notifications off).</summary>
    public bool IsHousehold => Id == HouseholdId;
    public bool FromHeader { get; set; }
}

public static class IdentityMiddleware
{
    public static IApplicationBuilder UseHaIdentity(this IApplicationBuilder app) => app.Use(async (ctx, next) =>
    {
        // Static files and health checks do not need a user (and must not touch the database).
        var path = ctx.Request.Path;
        if (path.StartsWithSegments("/lib") || path.StartsWithSegments("/css") || path.StartsWithSegments("/js") ||
            path.Equals("/healthz") || path.Equals("/favicon.ico"))
        {
            await next();
            return;
        }

        var mode = ctx.RequestServices.GetRequiredService<AppMode>();
        var user = ctx.RequestServices.GetRequiredService<CurrentUser>();
        var headers = ctx.Request.Headers;
        string? id = headers["X-Remote-User-Id"].FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(id))
        {
            user.Id = id.Trim();
            user.FromHeader = true;
            string? display = headers["X-Remote-User-Display-Name"].FirstOrDefault();
            string? name = headers["X-Remote-User-Name"].FirstOrDefault();
            user.DisplayName = !string.IsNullOrWhiteSpace(display) ? display
                : !string.IsNullOrWhiteSpace(name) ? name
                : "User " + user.Id[..Math.Min(6, user.Id.Length)];
        }
        else if (!mode.AddonMode)
        {
            user.Id = CurrentUser.DevId;
            user.DisplayName = "Developer";
        }
        else if (ctx.Request.Cookies[CurrentUser.ChooseCookie] is { Length: > 0 and <= 100 } chosen)
        {
            user.Id = chosen;
            user.DisplayName = chosen;
        }

        var db = ctx.RequestServices.GetRequiredService<MedStockDb>();
        var clock = ctx.RequestServices.GetRequiredService<IClock>();
        await EnsureUserAsync(db, user, clock.Now, ctx.RequestAborted);
        await next();
    });

    internal static async Task EnsureUserAsync(MedStockDb db, CurrentUser user, DateTime now, CancellationToken ct)
    {
        var row = await db.Users.FirstOrDefaultAsync(u => u.Id == user.Id, ct);
        if (row is null)
        {
            row = new User { Id = user.Id, DisplayName = user.DisplayName, FirstSeen = now, LastSeen = now };
            db.Users.Add(row);
            db.UserSettings.Add(new UserSettings { UserId = user.Id, NotificationsEnabled = !user.IsHousehold });
            try { await db.SaveChangesAsync(ct); }
            catch (DbUpdateException) { db.ChangeTracker.Clear(); } // concurrent first request of the same user
            return;
        }
        if (!user.FromHeader && row.DisplayName.Length > 0) user.DisplayName = row.DisplayName;
        bool changed = false;
        if (user.FromHeader && row.DisplayName != user.DisplayName) { row.DisplayName = user.DisplayName; changed = true; }
        if (now - row.LastSeen > TimeSpan.FromHours(1)) { row.LastSeen = now; changed = true; }
        if (changed) await db.SaveChangesAsync(ct);
    }
}
