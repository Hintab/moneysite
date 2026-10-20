using Microsoft.EntityFrameworkCore;
using MoneySite.Data;

namespace MoneySite.Services;

public record AccountHistory(int AccountId, string Name, DateTime First, DateTime Last);

/// The date range every report and list uses. Banks return different amounts of history
/// (e.g. 6 months for one, 2 years for another), so by default everything starts on the
/// day the shortest account history starts — that way all accounts cover the same dates.
/// Older transactions are only hidden, never deleted; the setting can be turned off.
public class DataWindow
{
    private const string AlignKey = "AlignHistory";
    private readonly IDbContextFactory<MoneyContext> _dbf;
    public DataWindow(IDbContextFactory<MoneyContext> dbf) => _dbf = dbf;

    public async Task<bool> IsAlignedAsync()
    {
        await using var db = await _dbf.CreateDbContextAsync();
        var v = await db.AppSettings.Where(s => s.Key == AlignKey).Select(s => s.Value).FirstOrDefaultAsync();
        return v != "false";
    }

    public async Task SetAlignedAsync(bool aligned)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        var s = await db.AppSettings.FindAsync(AlignKey);
        if (s is null) db.AppSettings.Add(s = new AppSetting { Key = AlignKey });
        s.Value = aligned ? "true" : "false";
        await db.SaveChangesAsync();
    }

    /// First and last transaction date of every account that has transactions.
    public async Task<List<AccountHistory>> HistoriesAsync()
    {
        await using var db = await _dbf.CreateDbContextAsync();
        var spans = await db.Transactions.GroupBy(t => t.AccountId)
            .Select(g => new { g.Key, First = g.Min(t => t.Date), Last = g.Max(t => t.Date) }).ToListAsync();
        var names = await db.Accounts.AsNoTracking().ToDictionaryAsync(a => a.Id, a => a.Name);
        return spans.Select(s => new AccountHistory(s.Key, names.GetValueOrDefault(s.Key, "?"), s.First.Date, s.Last.Date))
            .OrderBy(h => h.First).ToList();
    }

    /// The shared start date (latest "first transaction" across accounts), or null when
    /// the setting is off or there's only one account.
    public async Task<DateTime?> StartAsync()
    {
        if (!await IsAlignedAsync()) return null;
        var h = await HistoriesAsync();
        return h.Count > 1 ? h.Max(x => x.First) : null;
    }

    /// Clamps a range start to the window.
    public async Task<DateTime> ClampAsync(DateTime from) =>
        await StartAsync() is DateTime start && start > from ? start : from;
}
