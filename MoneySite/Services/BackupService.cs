using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using MoneySite.Data;

namespace MoneySite.Services;

/// Copies the whole database out (download) and back in (restore), so moving to a new
/// machine or container is done in the browser instead of copying files by hand.
/// SQLite's backup API is used both ways, so a copy is consistent even while the app runs.
public class BackupService
{
    private static readonly string[] RequiredTables = { "Accounts", "Transactions", "PlaidItems", "Categories" };

    private readonly IDbContextFactory<MoneyContext> _dbf;
    private readonly Categorizer _categorizer;

    public BackupService(IDbContextFactory<MoneyContext> dbf, Categorizer categorizer, string dbPath)
    {
        _dbf = dbf;
        _categorizer = categorizer;
        DbPath = dbPath;
    }

    public string DbPath { get; }

    /// Writes a consistent copy of the live database to a temp file and returns its path.
    /// The caller deletes it.
    public string CreateBackupFile()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"moneysite-backup-{Guid.NewGuid():N}.db");
        Copy(DbPath, tmp);
        return tmp;
    }

    /// Replaces everything in the live database with the uploaded backup. The current data is
    /// first saved next to the database as money.before-restore-…db. Returns an error to show,
    /// or null on success.
    public async Task<string?> RestoreAsync(Stream upload, CancellationToken ct = default)
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"moneysite-restore-{Guid.NewGuid():N}.db");
        try
        {
            await using (var file = File.Create(tmp))
                await upload.CopyToAsync(file, ct);

            if (Check(tmp) is string problem) return problem;

            var dir = Path.GetDirectoryName(DbPath)!;
            Copy(DbPath, Path.Combine(dir, $"money.before-restore-{DateTime.Now:yyyyMMdd-HHmmss}.db"));
            Copy(tmp, DbPath);
            // Pooled connections may hold the old schema; start fresh.
            SqliteConnection.ClearAllPools();

            // A backup from an older version gets the same upgrades as at startup.
            await using (var db = await _dbf.CreateDbContextAsync(ct))
            {
                DbSeed.EnsureSchema(db);
                DbSeed.EnsureSeed(db);
                DbSeed.BackfillMerchants(db);
            }
            await _categorizer.MoveCompanySettingsToKeywordsAsync();
            await _categorizer.CategorizeAsync(reapplyAll: true);
            return null;
        }
        catch (SqliteException ex)
        {
            return $"Couldn't restore that file: {ex.Message}";
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    /// Null if the file is a readable MoneySite database, otherwise why not.
    private static string? Check(string path)
    {
        try
        {
            using var c = new SqliteConnection(Conn(path, readOnly: true));
            c.Open();
            using (var cmd = c.CreateCommand())
            {
                cmd.CommandText = "PRAGMA quick_check";
                if (cmd.ExecuteScalar() as string != "ok") return "That file is damaged, so nothing was changed.";
            }
            var tables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var cmd = c.CreateCommand())
            {
                cmd.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table'";
                using var r = cmd.ExecuteReader();
                while (r.Read()) tables.Add(r.GetString(0));
            }
            return RequiredTables.All(tables.Contains) ? null : "That isn't a MoneySite backup, so nothing was changed.";
        }
        catch (SqliteException)
        {
            return "That isn't a MoneySite backup, so nothing was changed.";
        }
    }

    private static void Copy(string from, string to)
    {
        using var src = new SqliteConnection(Conn(from));
        using var dst = new SqliteConnection(Conn(to));
        src.Open();
        dst.Open();
        src.BackupDatabase(dst);
    }

    private static string Conn(string path, bool readOnly = false) => new SqliteConnectionStringBuilder
    {
        DataSource = path,
        Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWriteCreate,
        Pooling = false,
    }.ToString();
}
