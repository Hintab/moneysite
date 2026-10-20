using Microsoft.EntityFrameworkCore;
using MoneySite.Data;

namespace MoneySite.Services;

public record MerchantSummary(string Name, int Count, decimal Spent, decimal Received, DateTime Last,
    int? CategoryId, string? TopCategory, string? TopCategoryColor, int? FixedCategoryId, int Uncategorized);

/// Grouping transactions by company: listing, combining/renaming, and per-company categories.
public class MerchantService
{
    private readonly IDbContextFactory<MoneyContext> _dbf;
    private readonly Categorizer _categorizer;
    private readonly DataWindow _window;

    public MerchantService(IDbContextFactory<MoneyContext> dbf, Categorizer categorizer, DataWindow window)
    {
        _dbf = dbf;
        _categorizer = categorizer;
        _window = window;
    }

    /// Every company with its totals, optionally limited to a date range.
    public async Task<List<MerchantSummary>> ListAsync(DateTime? from = null, DateTime? toExclusive = null)
    {
        from = await _window.ClampAsync(from ?? DateTime.MinValue);
        await using var db = await _dbf.CreateDbContextAsync();
        var ignore = await ReportService.IgnoreIdAsync(db);
        var q = db.Transactions.AsNoTracking().Where(t => t.Merchant != null && t.CategoryId != ignore);
        if (from is DateTime f) q = q.Where(t => t.Date >= f);
        if (toExclusive is DateTime e) q = q.Where(t => t.Date < e);
        var tx = await q.Select(t => new { t.Merchant, t.Date, t.Amount, t.CategoryId }).ToListAsync();
        var cats = await db.Categories.AsNoTracking().ToDictionaryAsync(c => c.Id);
        var uncategorizedId = cats.Values.FirstOrDefault(c => c.Name == DbSeed.Uncategorized)?.Id;
        var fixedCats = await db.Merchants.AsNoTracking().Where(m => m.CategoryId != null)
            .ToDictionaryAsync(m => m.Name, m => m.CategoryId);

        return tx.GroupBy(t => t.Merchant!)
            .Select(g =>
            {
                var top = g.Where(t => t.CategoryId != null).GroupBy(t => t.CategoryId!.Value)
                    .OrderByDescending(c => c.Count()).Select(c => (int?)c.Key).FirstOrDefault();
                var cat = top is int id && cats.TryGetValue(id, out var c) ? c : null;
                return new MerchantSummary(g.Key, g.Count(),
                    -g.Where(t => t.Amount < 0).Sum(t => t.Amount),
                    g.Where(t => t.Amount > 0).Sum(t => t.Amount),
                    g.Max(t => t.Date), top, cat?.Name, cat?.Color, fixedCats.GetValueOrDefault(g.Key),
                    g.Count(t => t.CategoryId is null || t.CategoryId == uncategorizedId));
            })
            .ToList();
    }

    /// Combines several companies into one (or renames one), now and for future syncs.
    /// Returns the name actually used (an existing company's spelling wins over a case-only difference).
    public async Task<string> CombineAsync(IReadOnlyCollection<string> names, string target)
    {
        target = target.Trim();
        if (target.Length == 0 || names.Count == 0) return target;

        await using (var db = await _dbf.CreateDbContextAsync())
        {
            var existing = await db.Transactions.Where(t => t.Merchant != null)
                .Select(t => t.Merchant!).Distinct().ToListAsync();
            target = existing.FirstOrDefault(n => n.Equals(target, StringComparison.OrdinalIgnoreCase)
                                                  && !names.Contains(n)) ?? target;
            var from = names.Where(n => n != target).ToList();
            if (from.Count == 0) return target;

            // Every raw name that currently lands on one of these companies now points at the target.
            var raws = await db.Transactions.Where(t => from.Contains(t.Merchant!) && t.MerchantRaw != null)
                .Select(t => t.MerchantRaw!).Distinct().ToListAsync();
            var aliases = await db.MerchantAliases.Where(a => raws.Contains(a.Raw) || from.Contains(a.Name)).ToListAsync();
            foreach (var raw in raws)
            {
                var alias = aliases.FirstOrDefault(a => a.Raw == raw);
                if (alias is null) db.MerchantAliases.Add(alias = new MerchantAlias { Raw = raw });
                alias.Name = target;
            }
            foreach (var a in aliases.Where(a => from.Contains(a.Name))) a.Name = target;
            // An alias from a name to itself is pointless.
            foreach (var a in aliases.Where(a => a.Raw == a.Name)) db.MerchantAliases.Remove(a);
            foreach (var e in db.ChangeTracker.Entries<MerchantAlias>().Where(e => e.State == EntityState.Added && e.Entity.Raw == e.Entity.Name).ToList())
                e.State = EntityState.Detached;

            // Keep a category setting: the target's own, else the busiest combined company's.
            var settings = await db.Merchants.Where(m => from.Contains(m.Name) || m.Name == target).ToListAsync();
            var keep = settings.FirstOrDefault(m => m.Name == target);
            if (keep is null)
            {
                keep = settings.FirstOrDefault(m => m.CategoryId != null);
                if (keep is not null) keep.Name = target;
            }
            foreach (var m in settings.Where(m => m != keep)) db.Merchants.Remove(m);

            await db.SaveChangesAsync();
            await db.Transactions.Where(t => from.Contains(t.Merchant!))
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.Merchant, target));
        }
        await _categorizer.CategorizeAsync(reapplyAll: true);
        return target;
    }

    /// Applies combine/rename history to a raw name from a new sync.
    public static string Resolve(string raw, IReadOnlyDictionary<string, string> aliases) =>
        aliases.TryGetValue(raw, out var name) ? name : raw;
}
