using System.Reflection;
using MedStock.Web.Domain;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace MedStock.Web.Data;

/// <summary>Startup order of §4: lock cleanup, backup before pending migrations, migrate, seed.</summary>
public static class DbInitializer
{
    public static string AppVersion =>
        typeof(DbInitializer).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? "0.0.0";

    public static async Task InitializeAsync(IServiceProvider services, AppPaths paths, ILogger logger, CancellationToken ct = default)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MedStockDb>();
        string? backupPath = null;
        try
        {
            await db.Database.OpenConnectionAsync(ct);
            try
            {
                // (a) A single process owns the database: any lock row at startup is a leftover.
                var conn = db.Database.GetDbConnection();
                await using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT count(*) FROM sqlite_master WHERE type='table' AND name='__EFMigrationsLock'";
                    if (Convert.ToInt64(await cmd.ExecuteScalarAsync(ct)) > 0)
                    {
                        cmd.CommandText = "DELETE FROM \"__EFMigrationsLock\"";
                        await cmd.ExecuteNonQueryAsync(ct);
                    }
                }

                // (b) Backup before applying migrations to an existing database.
                var pending = (await db.Database.GetPendingMigrationsAsync(ct)).ToList();
                var applied = (await db.Database.GetAppliedMigrationsAsync(ct)).ToList();
                if (pending.Count > 0 && applied.Count > 0)
                {
                    Directory.CreateDirectory(paths.BackupDir);
                    backupPath = Path.Combine(paths.BackupDir, $"medstock-{AppVersion}-{DateTime.Now:yyyyMMddHHmm}.db");
                    if (File.Exists(backupPath)) File.Delete(backupPath);
                    await using (var cmd = conn.CreateCommand())
                    {
                        cmd.CommandText = "VACUUM INTO $path";
                        cmd.Parameters.Add(new SqliteParameter("$path", backupPath));
                        await cmd.ExecuteNonQueryAsync(ct);
                    }
                    logger.LogInformation("Database backup before migration written to {Path}", backupPath);
                    foreach (var old in new DirectoryInfo(paths.BackupDir).GetFiles("medstock-*.db")
                                 .OrderByDescending(f => f.LastWriteTimeUtc).Skip(3))
                        old.Delete();
                }

                // (c) Migrate.
                if (pending.Count > 0) logger.LogInformation("Applying {Count} database migration(s)", pending.Count);
                await db.Database.MigrateAsync(ct);
            }
            finally
            {
                await db.Database.CloseConnectionAsync();
            }

            // (d) Seed the global settings row.
            var settings = await db.GlobalSettings.FindAsync([1], ct);
            if (settings is null)
            {
                settings = new GlobalSettings();
                db.GlobalSettings.Add(settings);
            }
            settings.WrittenByVersion = AppVersion;
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            logger.LogCritical(ex, "Database initialization failed. Backup copy: {Path}", backupPath ?? "(none)");
            throw;
        }
    }
}

public sealed class AppPaths(string dataDir)
{
    public string DataDir { get; } = dataDir;
    public string DbPath => Path.Combine(DataDir, "medstock.db");
    public string PhotosDir => Path.Combine(DataDir, "photos");
    public string KeysDir => Path.Combine(DataDir, "keys");
    public string BackupDir => Path.Combine(DataDir, "backup");
    public string PhotoPath(int medicationId) => Path.Combine(PhotosDir, $"{medicationId}.jpg");
}
