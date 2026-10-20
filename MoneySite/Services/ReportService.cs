using Microsoft.EntityFrameworkCore;
using MoneySite.Data;

namespace MoneySite.Services;

public record ChartPoint(DateTime Date, decimal Value);
public record AccountBalance(Account Account, decimal Value, int TransactionCount, DateTime? SnapshotDate);
/// Value and Change are signed by effect on net worth (a card balance is negative).
public record AccountSummary(Account Account, decimal Value, decimal? Change, decimal In, decimal Out,
    DateTime? AsOf, List<ChartPoint> Trend);
public record CategoryTotal(int? CategoryId, string Name, string Color, decimal Total, int Count);
public record MonthFlow(DateTime Month, decimal Income, decimal Spending);

/// How a card payment compares with what was owed.
public static class PayStatus
{
    public const string PaidOff = "Paid off";          // cleared the whole balance
    public const string InFull = "Paid in full";       // matched a statement balance exactly
    public const string PaidDown = "Paid down";        // a partial payment
    public const string NoPayment = "No payment";
    public const string NothingOwed = "Nothing owed";
    public const string Unknown = "Not enough history";
}

/// One card in one calendar month. Owed amounts are positive (what you owe); Credits are refunds/rewards.
/// Advances = money moved out of the card to another of your accounts (cash advance / balance transfer).
public record CardMonth(DateTime Month, decimal OwedAtStart, decimal Purchases, decimal Advances, decimal Payments, decimal Credits,
    decimal OwedAtEnd, string Status);

/// A payment credited to a card. StatementBalance is the balance it paid (when it matched one).
public record CardPaymentRow(DateTime Date, decimal Amount, string? FromAccount, DateTime? FromDate,
    string Status, decimal? StatementBalance, DateTime? StatementDate);
public record CardReport(Account Card, decimal OwedNow, List<CardMonth> Months, List<CardPaymentRow> Payments);
public record UnlinkedCardPayment(DateTime Date, string Merchant, decimal Amount, string FromAccount);

public record HoldingRow(string Name, string? Ticker, string? Type, decimal Quantity, decimal Price, decimal Value, decimal? CostBasis, DateTime AsOf);
public record ActivityRow(DateTime Date, string Name, string Type, string Subtype, string? Security, decimal Quantity, decimal Amount, bool IsContribution);
public record ContributionMonth(DateTime Month, decimal Amount);

/// One retirement/brokerage account. Contributions/Dividends/Fees are positive amounts.
/// Growth = change in value since the first recorded balance, minus money contributed since then.
public record InvestmentReport(
    Account Account, decimal Value, DateTime? AsOf, List<HoldingRow> Holdings, List<ActivityRow> Activity,
    List<ContributionMonth> ContributionsByMonth, decimal ContributionsYtd, decimal Contributions12m,
    decimal Dividends12m, decimal Fees12m, decimal? UnrealizedGain, decimal UncostedValue, DateTime? GrowthSince, decimal? Growth,
    List<ChartPoint> ValueHistory);

/// Totals for a period. Averages use complete months only when the period has any.
public record FlowSummary(decimal Income, decimal Spending, decimal AvgIncome, decimal AvgSpending, int Months, int AvgMonths)
{
    public decimal Net => Income - Spending;
    public decimal? SavingsRate => Income > 0 ? Net / Income : null;
}
public record NamedAmount(string Name, string Color, decimal Amount, int Count, int? CategoryId = null);
public record BigTx(DateTime Date, string Name, string Account, decimal Amount, string? Category);
public record RecurringItem(string Merchant, string? Category, decimal Monthly, int MonthsSeen, DateTime Last);
public record MonthPace(decimal SpentSoFar, int DaysElapsed, int DaysInMonth, decimal Projected, decimal TypicalMonth);

public record CashFlowReport(
    DateTime From, DateTime ToExclusive, FlowSummary Current, FlowSummary? Previous,
    List<MonthFlow> Months, List<NamedAmount> IncomeSources, List<NamedAmount> SpendingByCategory,
    List<NamedAmount> TopMerchants, List<RecurringItem> Recurring, List<BigTx> LargestExpenses, MonthPace? Pace);

/// All money math for the reports.
///
/// Sign convention everywhere: negative amounts are money out, positive money in.
/// "Spending" = money out in any category except Transfer and Income, minus refunds in that
///              category. Paying a credit card from checking is a Transfer on both sides, so
///              only the card purchases themselves count — nothing is counted twice.
/// "Income"   = money in that is categorized Income (or not categorized at all).
///              Transfers between your accounts and refunds are not income.
public class ReportService
{
    private readonly IDbContextFactory<MoneyContext> _dbf;
    private readonly DataWindow _window;

    public ReportService(IDbContextFactory<MoneyContext> dbf, DataWindow window)
    {
        _dbf = dbf;
        _window = window;
    }

    private record Tx(int AccountId, DateTime Date, decimal Amount, int? CategoryId);

    /// Balance of every account today, liabilities as negative values.
    public async Task<List<AccountBalance>> AccountBalancesAsync()
    {
        await using var db = await _dbf.CreateDbContextAsync();
        var (accounts, ledgers) = await LoadLedgersAsync(db);
        var today = DateTime.Today;
        var counts = await db.Transactions.GroupBy(t => t.AccountId)
            .Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N);
        return accounts.Select(a => new AccountBalance(a, ledgers[a.Id].ValueAt(today),
                counts.GetValueOrDefault(a.Id), ledgers[a.Id].LatestSnapshot))
            .ToList();
    }

    /// Each account's balance today, how it changed over the last <paramref name="days"/> days,
    /// what came in and went out in that time, and a daily trend line. Change is null when the
    /// account's history doesn't reach back that far (e.g. a retirement account that's only
    /// been synced recently).
    public async Task<List<AccountSummary>> AccountSummariesAsync(int days = 30)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        var (accounts, ledgers) = await LoadLedgersAsync(db);
        var today = DateTime.Today;
        var since = today.AddDays(-days);
        var ignore = await IgnoreIdAsync(db);
        // Summed in memory: SQLite can't aggregate decimals.
        var flows = (await db.Transactions.AsNoTracking().Where(t => t.Date > since && t.CategoryId != ignore)
                .Select(t => new { t.AccountId, t.Amount }).ToListAsync())
            .GroupBy(t => t.AccountId)
            .ToDictionary(g => g.Key, g => new
            {
                In = g.Where(t => t.Amount > 0).Sum(t => t.Amount),
                Out = -g.Where(t => t.Amount < 0).Sum(t => t.Amount),
            });

        return accounts.Select(a =>
        {
            var l = ledgers[a.Id];
            var value = l.ValueAt(today);
            var covered = l.FirstDate is DateTime f && f <= since;
            var trend = Enumerable.Range(0, days + 1)
                .Select(i => since.AddDays(i))
                .Where(d => l.FirstDate is DateTime fd && d >= fd)
                .Select(d => new ChartPoint(d, l.ValueAt(d))).ToList();
            var flow = flows.GetValueOrDefault(a.Id);
            return new AccountSummary(a, value, covered ? value - l.ValueAt(since) : null,
                flow?.In ?? 0, flow?.Out ?? 0, l.LatestSnapshot, trend);
        }).ToList();
    }

    /// Net worth over time: month-end points (or weekly when there's under ~4 months
    /// of data), ending today.
    ///
    /// Each account's value on a day is anchored on its nearest known balance (recorded by
    /// a Plaid sync) and walked forward/back by the transactions in
    /// between. An account Plaid never reported a balance for starts from zero.
    public async Task<List<ChartPoint>> NetWorthHistoryAsync()
    {
        await using var db = await _dbf.CreateDbContextAsync();
        var (accounts, ledgers) = await LoadLedgersAsync(db);
        var firstDates = ledgers.Values.Select(l => l.FirstDate).OfType<DateTime>().ToList();
        if (accounts.Count == 0 || firstDates.Count == 0) return new();

        var start = await _window.ClampAsync(firstDates.Min());
        var today = DateTime.Today;
        var dates = new List<DateTime>();
        if ((today - start).TotalDays <= 120)
        {
            for (var d = today; d >= start.AddDays(-7); d = d.AddDays(-7)) dates.Insert(0, d);
        }
        else
        {
            for (var m = new DateTime(start.Year, start.Month, 1); m <= today; m = m.AddMonths(1))
                dates.Add(m.AddMonths(1).AddDays(-1) < today ? m.AddMonths(1).AddDays(-1) : today);
            dates.Insert(0, new DateTime(start.Year, start.Month, 1).AddDays(-1));
        }
        return dates.Distinct()
            .Select(d => new ChartPoint(d, accounts.Sum(a => ledgers[a.Id].ValueAt(d))))
            .ToList();
    }

    public async Task<List<CategoryTotal>> SpendingByCategoryAsync(DateTime from, DateTime toExclusive)
    {
        from = await _window.ClampAsync(from);
        await using var db = await _dbf.CreateDbContextAsync();
        var cats = await db.Categories.AsNoTracking().ToDictionaryAsync(c => c.Id);
        var kinds = new Kinds(cats.Values);
        var tx = await LoadTxAsync(db, from, toExclusive);

        return tx.Where(t => kinds.CountsAsSpending(t.CategoryId))
            .GroupBy(t => t.CategoryId)
            .Select(g =>
            {
                var c = g.Key is int id && cats.TryGetValue(id, out var cc) ? cc : null;
                return new CategoryTotal(g.Key, c?.Name ?? DbSeed.Uncategorized, c?.Color ?? "#bdc3c7",
                    g.Sum(kinds.Spending), g.Count(t => t.Amount < 0));
            })
            .Where(x => x.Total > 0)
            .OrderByDescending(x => x.Total)
            .ToList();
    }

    public async Task<(decimal Income, decimal Spending)> TotalsAsync(DateTime from, DateTime toExclusive)
    {
        var flows = await CashFlowAsync(from, toExclusive);
        return (flows.Sum(f => f.Income), flows.Sum(f => f.Spending));
    }

    /// Income and spending for each of the last <paramref name="months"/> months (oldest first).
    public Task<List<MonthFlow>> CashFlowAsync(int months)
    {
        var thisMonth = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
        return CashFlowAsync(thisMonth.AddMonths(-(months - 1)), thisMonth.AddMonths(1));
    }

    private async Task<List<MonthFlow>> CashFlowAsync(DateTime from, DateTime toExclusive)
    {
        from = await _window.ClampAsync(from);
        await using var db = await _dbf.CreateDbContextAsync();
        var kinds = new Kinds(await db.Categories.AsNoTracking().ToListAsync());
        var tx = await LoadTxAsync(db, from, toExclusive);

        var result = new List<MonthFlow>();
        for (var m = new DateTime(from.Year, from.Month, 1); m < toExclusive; m = m.AddMonths(1))
        {
            var me = m.AddMonths(1);
            var inMonth = tx.Where(t => t.Date >= m && t.Date < me && t.Date >= from && t.Date < toExclusive).ToList();
            result.Add(new MonthFlow(m, inMonth.Sum(kinds.Income), kinds.SpendingTotal(inMonth)));
        }
        return result;
    }

    /// How each transaction counts toward spending and income, by its category.
    private sealed class Kinds
    {
        private readonly int? _transfer, _income, _uncategorized, _ignore;

        public Kinds(IEnumerable<Category> cats)
        {
            foreach (var c in cats)
            {
                if (c.Name == DbSeed.Transfer) _transfer = c.Id;
                else if (c.Name == DbSeed.Income) _income = c.Id;
                else if (c.Name == DbSeed.Uncategorized) _uncategorized = c.Id;
                else if (c.Name == DbSeed.Ignore) _ignore = c.Id;
            }
        }

        private bool Unknown(int? cat) => cat is null || cat == _uncategorized;

        public bool CountsAsSpending(int? cat) => cat != _transfer && cat != _income && cat != _ignore;

        /// Money out counts as spending; a refund (money in) in a spending category reduces it.
        /// Money in that isn't categorized is treated as income, not as a refund.
        public decimal Spending(Tx t)
        {
            if (!CountsAsSpending(t.CategoryId)) return 0;
            if (Unknown(t.CategoryId)) return t.Amount < 0 ? -t.Amount : 0;
            return -t.Amount;
        }

        public decimal Income(Tx t) =>
            t.Amount > 0 && (t.CategoryId == _income || Unknown(t.CategoryId)) ? t.Amount : 0;

        /// Total spending, never letting one category's net refunds go below zero.
        public decimal SpendingTotal(IEnumerable<Tx> tx) =>
            tx.GroupBy(t => t.CategoryId).Sum(g => Math.Max(0, g.Sum(Spending)));
    }

    /// Money put into an investment account (employee and employer alike). Plaid usually marks it
    /// subtype "contribution"; Empower instead names it e.g. "Fund X - BEFORETAX CONTRIBUTION".
    /// Moves between funds ("… TRANSFER") are not contributions.
    public static bool IsContribution(string subtype, string name) =>
        subtype.Contains("contribution", StringComparison.OrdinalIgnoreCase)
        || (name.Contains("contribution", StringComparison.OrdinalIgnoreCase)
            && !name.Contains("transfer", StringComparison.OrdinalIgnoreCase));

    private static bool IsCash(Security? s) =>
        s is not null && (string.Equals(s.Type, "cash", StringComparison.OrdinalIgnoreCase)
                          || (s.Ticker?.StartsWith("CUR:", StringComparison.OrdinalIgnoreCase) ?? false));

    /// Retirement and brokerage accounts: holdings, contributions, dividends, fees and growth.
    /// Investment activity comes from Plaid Investments and is never part of income or spending.
    public async Task<List<InvestmentReport>> InvestmentsAsync()
    {
        await using var db = await _dbf.CreateDbContextAsync();
        var accounts = await db.Accounts.AsNoTracking().Where(a => a.Type == AccountType.Investment || a.Type == AccountType.Retirement).OrderBy(a => a.Name).ToListAsync();
        if (accounts.Count == 0) return new();
        var ids = accounts.Select(a => a.Id).ToList();
        var holdings = await db.Holdings.AsNoTracking().Include(h => h.Security).Where(h => ids.Contains(h.AccountId)).ToListAsync();
        var activity = await db.InvestmentTransactions.AsNoTracking().Include(t => t.Security).Where(t => ids.Contains(t.AccountId)).ToListAsync();
        var snaps = await db.BalanceSnapshots.AsNoTracking().Where(s => ids.Contains(s.AccountId)).ToListAsync();

        var today = DateTime.Today;
        var thisMonth = new DateTime(today.Year, today.Month, 1);
        var yearAgo = today.AddYears(-1);
        var result = new List<InvestmentReport>();
        foreach (var a in accounts)
        {
            var mine = activity.Where(t => t.AccountId == a.Id).ToList();
            // 401(k)s report each fund once per money source (before-tax, match, Roth): show one row per fund.
            var hs = holdings.Where(h => h.AccountId == a.Id)
                .GroupBy(h => (h.Security?.Name ?? "?", h.Security?.Ticker))
                .Select(g => new HoldingRow(g.Key.Item1, g.Key.Ticker, g.First().Security?.Type,
                    g.Sum(h => h.Quantity), g.Max(h => h.Price), g.Sum(h => h.Value),
                    g.All(h => h.CostBasis is not null) ? g.Sum(h => h.CostBasis!.Value)
                        : IsCash(g.First().Security) ? g.Sum(h => h.Value) : null,   // cash costs what it's worth
                    g.Max(h => h.AsOf)))
                .OrderByDescending(h => h.Value).ToList();
            var history = snaps.Where(s => s.AccountId == a.Id).GroupBy(s => s.Date.Date)
                .Select(g => new ChartPoint(g.Key, g.OrderBy(s => s.Id).Last().Balance)).OrderBy(p => p.Date).ToList();
            var value = history.Count > 0 ? history[^1].Value : hs.Sum(h => h.Value);

            static bool IsContribution(InvestmentTransaction t) => ReportService.IsContribution(t.Subtype, t.Name);
            static bool IsDividend(InvestmentTransaction t) => t.Subtype.Contains("dividend", StringComparison.OrdinalIgnoreCase)
                                                               || t.Subtype.Equals("interest", StringComparison.OrdinalIgnoreCase);
            static bool IsFee(InvestmentTransaction t) => t.Type.Equals("fee", StringComparison.OrdinalIgnoreCase)
                                                          || t.Subtype.Contains("fee", StringComparison.OrdinalIgnoreCase);
            var contributions = mine.Where(IsContribution).ToList();
            var byMonth = Enumerable.Range(0, 12).Select(i => thisMonth.AddMonths(i - 11))
                .Select(m => new ContributionMonth(m, contributions.Where(t => t.Date >= m && t.Date < m.AddMonths(1)).Sum(t => Math.Abs(t.Amount))))
                .ToList();

            // Gain = value minus what was paid, over holdings with a known cost. Shown when those make up
            // at least 95% of the account (e.g. a few free warrants with no cost don't hide it).
            var costed = hs.Where(h => h.CostBasis is not null).ToList();
            var uncosted = hs.Where(h => h.CostBasis is null).Sum(h => h.Value);
            var total = hs.Sum(h => h.Value);
            decimal? unrealized = costed.Count > 0 && (total <= 0 || uncosted / total <= 0.05m)
                ? costed.Sum(h => h.Value - h.CostBasis!.Value) : null;
            DateTime? growthSince = history.Count > 1 ? history[0].Date : null;
            decimal? growth = growthSince is DateTime gs
                ? value - history[0].Value - contributions.Where(t => t.Date > gs).Sum(t => Math.Abs(t.Amount))
                : null;

            result.Add(new InvestmentReport(a, value, history.Count > 0 ? history[^1].Date : hs.FirstOrDefault()?.AsOf,
                hs,
                mine.OrderByDescending(t => t.Date).ThenByDescending(t => t.Id)
                    .Select(t => new ActivityRow(t.Date, t.Name, t.Type, t.Subtype, t.Security?.Name, t.Quantity, t.Amount, IsContribution(t))).ToList(),
                byMonth,
                contributions.Where(t => t.Date.Year == today.Year).Sum(t => Math.Abs(t.Amount)),
                contributions.Where(t => t.Date > yearAgo).Sum(t => Math.Abs(t.Amount)),
                mine.Where(t => IsDividend(t) && t.Date > yearAgo).Sum(t => Math.Abs(t.Amount)),
                mine.Where(t => IsFee(t) && t.Date > yearAgo).Sum(t => Math.Abs(t.Amount) + Math.Abs(t.Fees)),
                unrealized, uncosted, growthSince, growth, history));
        }
        return result;
    }

    /// Everything for the Income vs Spending page, for whole months from <paramref name="from"/>
    /// up to <paramref name="toExclusive"/> (clamped to the history window), plus the same-length
    /// period just before it for comparison.
    public async Task<CashFlowReport> CashFlowReportAsync(DateTime from, DateTime toExclusive)
    {
        from = await _window.ClampAsync(from);
        var windowStart = await _window.StartAsync();
        await using var db = await _dbf.CreateDbContextAsync();
        var cats = await db.Categories.AsNoTracking().ToDictionaryAsync(c => c.Id);
        var kinds = new Kinds(cats.Values);
        var accounts = await db.Accounts.AsNoTracking().ToDictionaryAsync(a => a.Id, a => a.Name);
        string CatName(int? id) => id is int i && cats.TryGetValue(i, out var c) ? c.Name : DbSeed.Uncategorized;
        string CatColor(int? id) => id is int i && cats.TryGetValue(i, out var c) ? c.Color : "#bdc3c7";

        // Previous period of the same number of months, if it's inside the window.
        var firstMonth = new DateTime(from.Year, from.Month, 1);
        var monthCount = Math.Max(1, (toExclusive.Year - firstMonth.Year) * 12 + toExclusive.Month - firstMonth.Month);
        var prevFrom = firstMonth.AddMonths(-monthCount);
        var hasPrev = windowStart is null || prevFrom >= new DateTime(windowStart.Value.Year, windowStart.Value.Month, 1);
        var loadFrom = await _window.ClampAsync(hasPrev ? prevFrom : from);
        // Recurring bills look at the last 6 months regardless of the period picked.
        var thisMonth = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
        var recurFrom = await _window.ClampAsync(thisMonth.AddMonths(-6));
        if (recurFrom < loadFrom) loadFrom = recurFrom;

        var all = (await db.Transactions.AsNoTracking()
                .Where(t => t.Date >= loadFrom && t.Date < (toExclusive > thisMonth.AddMonths(1) ? toExclusive : thisMonth.AddMonths(1)))
                .Select(t => new { t.AccountId, t.Date, t.Amount, t.CategoryId, t.Merchant, t.Description })
                .ToListAsync())
            .Select(t => (Tx: new Tx(t.AccountId, t.Date, t.Amount, t.CategoryId), Name: t.Merchant ?? t.Description))
            .ToList();
        var period = all.Where(t => t.Tx.Date >= from && t.Tx.Date < toExclusive).ToList();

        // Monthly flows and summaries.
        List<MonthFlow> Flows(DateTime f, DateTime to)
        {
            var list = new List<MonthFlow>();
            for (var m = new DateTime(f.Year, f.Month, 1); m < to; m = m.AddMonths(1))
            {
                var inMonth = all.Where(t => t.Tx.Date >= m && t.Tx.Date < m.AddMonths(1) && t.Tx.Date >= f && t.Tx.Date < to)
                    .Select(t => t.Tx).ToList();
                list.Add(new MonthFlow(m, inMonth.Sum(kinds.Income), kinds.SpendingTotal(inMonth)));
            }
            return list;
        }
        FlowSummary Summarize(List<MonthFlow> flows)
        {
            var complete = flows.Where(f => f.Month.AddMonths(1) <= DateTime.Today.AddDays(1)).ToList();
            var basis = complete.Count > 0 ? complete : flows;
            return new FlowSummary(flows.Sum(f => f.Income), flows.Sum(f => f.Spending),
                basis.Count > 0 ? basis.Average(f => f.Income) : 0, basis.Count > 0 ? basis.Average(f => f.Spending) : 0,
                flows.Count, basis.Count);
        }
        var months = Flows(from, toExclusive);
        var previous = hasPrev ? Summarize(Flows(prevFrom, firstMonth)) : null;

        // Income sources: money in that counts as income, grouped by payer.
        var incomeSources = period.Where(t => kinds.Income(t.Tx) > 0)
            .GroupBy(t => t.Name)
            .Select(g => new NamedAmount(g.Key, "#2a78d6", g.Sum(t => t.Tx.Amount), g.Count()))
            .OrderByDescending(x => x.Amount).ToList();

        var byCategory = period.Where(t => kinds.CountsAsSpending(t.Tx.CategoryId))
            .GroupBy(t => t.Tx.CategoryId)
            .Select(g => new NamedAmount(CatName(g.Key), CatColor(g.Key), g.Sum(t => kinds.Spending(t.Tx)), g.Count(t => t.Tx.Amount < 0), g.Key))
            .Where(x => x.Amount > 0).OrderByDescending(x => x.Amount).ToList();

        var topMerchants = period.Where(t => kinds.CountsAsSpending(t.Tx.CategoryId))
            .GroupBy(t => t.Name)
            .Select(g =>
            {
                var cat = g.GroupBy(t => t.Tx.CategoryId).OrderByDescending(c => c.Count()).First().Key;
                return new NamedAmount(g.Key, CatColor(cat), g.Sum(t => kinds.Spending(t.Tx)), g.Count(t => t.Tx.Amount < 0), cat);
            })
            .Where(x => x.Amount > 0).OrderByDescending(x => x.Amount).Take(12).ToList();

        BigTx Big((Tx Tx, string Name) t) => new(t.Tx.Date, t.Name, accounts.GetValueOrDefault(t.Tx.AccountId, ""), t.Tx.Amount, CatName(t.Tx.CategoryId));
        var largestExpenses = period.Where(t => kinds.Spending(t.Tx) > 0).OrderBy(t => t.Tx.Amount).Take(10).Select(Big).ToList();

        // Recurring: same company charged in at least 3 of the last 6 months, with steady amounts.
        var recurWindow = all.Where(t => t.Tx.Date >= recurFrom && kinds.Spending(t.Tx) > 0).ToList();
        var recurring = recurWindow.GroupBy(t => t.Name)
            .Select(g =>
            {
                var perMonth = g.GroupBy(t => new DateTime(t.Tx.Date.Year, t.Tx.Date.Month, 1))
                    .Select(m => m.Sum(t => kinds.Spending(t.Tx))).OrderBy(v => v).ToList();
                var median = perMonth.Count > 0 ? perMonth[perMonth.Count / 2] : 0;
                var steady = perMonth.Count > 0 && perMonth.All(v => median > 0 && Math.Abs(v - median) <= median * 0.35m);
                var cat = g.GroupBy(t => t.Tx.CategoryId).OrderByDescending(c => c.Count()).First().Key;
                return (Item: new RecurringItem(g.Key, CatName(cat), median, perMonth.Count, g.Max(t => t.Tx.Date)), steady);
            })
            .Where(x => x.Item.MonthsSeen >= 3 && x.steady)
            .Select(x => x.Item).OrderByDescending(x => x.Monthly).ToList();

        // This month's pace, when the period includes the current month.
        MonthPace? pace = null;
        if (toExclusive > thisMonth && from < thisMonth.AddMonths(1))
        {
            var cur = months.FirstOrDefault(m => m.Month == thisMonth);
            var daysIn = DateTime.DaysInMonth(thisMonth.Year, thisMonth.Month);
            var elapsed = DateTime.Today.Day;
            var typicalSource = Flows(await _window.ClampAsync(thisMonth.AddMonths(-6)), thisMonth);
            // Median, so one big bill doesn't make every other month look "on track".
            var sorted = typicalSource.Select(m => m.Spending).OrderBy(v => v).ToList();
            var typical = sorted.Count > 0 ? sorted[sorted.Count / 2] : 0;
            if (cur is not null)
                pace = new MonthPace(cur.Spending, elapsed, daysIn, elapsed > 0 ? cur.Spending / elapsed * daysIn : 0, typical);
        }

        return new CashFlowReport(from, toExclusive, Summarize(months), previous, months, incomeSources, byCategory,
            topMerchants, recurring, largestExpenses, pace);
    }

    /// Month-by-month view of every credit card: what was owed, what was bought, what was
    /// paid and from where — plus payments from checking to cards that aren't linked.
    public async Task<(List<CardReport> Cards, List<UnlinkedCardPayment> Unlinked)> CreditCardsAsync(int maxMonths = 12)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        var (accounts, ledgers) = await LoadLedgersAsync(db);
        var names = accounts.ToDictionary(a => a.Id, a => a.Name);
        var cats = await db.Categories.AsNoTracking().ToDictionaryAsync(c => c.Name, c => c.Id);
        var transfer = cats.GetValueOrDefault(DbSeed.Transfer);
        var ignore = cats.GetValueOrDefault(DbSeed.Ignore, -1);
        var thisMonth = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
        var from = await _window.ClampAsync(thisMonth.AddMonths(1 - maxMonths));
        var firstMonth = new DateTime(from.Year, from.Month, 1);

        // Whole calendar months, even when the history window starts mid-month.
        var tx = await db.Transactions.AsNoTracking().Where(t => t.Date >= firstMonth)
            .Select(t => new { t.Id, t.AccountId, t.Date, t.Amount, t.CategoryId, t.TransferPeerId, t.Merchant, t.Description })
            .ToListAsync();
        var byId = tx.ToDictionary(t => t.Id);
        // A payment's other half can sit just before the window start.
        var peerIds = tx.Where(t => t.TransferPeerId is int p && !byId.ContainsKey(p)).Select(t => t.TransferPeerId!.Value).ToList();
        var peers = (await db.Transactions.AsNoTracking().Where(t => peerIds.Contains(t.Id))
                .Select(t => new { t.Id, t.AccountId, t.Date }).ToListAsync())
            .ToDictionary(t => t.Id, t => (t.AccountId, t.Date));
        foreach (var t in tx) peers.TryAdd(t.Id, (t.AccountId, t.Date));

        var cards = new List<CardReport>();
        foreach (var card in accounts.Where(a => a.Type.IsLiability()))
        {
            var ledger = ledgers[card.Id];
            decimal OwedAt(DateTime d) => -ledger.ValueAt(d);
            var mine = tx.Where(t => t.AccountId == card.Id).ToList();

            var payList = mine.Where(t => t.Amount > 0 && t.CategoryId == transfer)
                .OrderByDescending(t => t.Date)
                .Select(t =>
                {
                    var (status, stmt, stmtDate) = ClassifyPayment(t.Date.Date, t.Amount, OwedAt, ledger.FirstTransaction);
                    var hasSrc = t.TransferPeerId is int p && peers.TryGetValue(p, out _);
                    var src = hasSrc ? peers[t.TransferPeerId!.Value] : default;
                    return new CardPaymentRow(t.Date, t.Amount, hasSrc ? names.GetValueOrDefault(src.AccountId) : null,
                        hasSrc ? src.Date : null, status, stmt, stmtDate);
                })
                .ToList();

            var months = new List<CardMonth>();
            for (var m = firstMonth; m <= thisMonth; m = m.AddMonths(1))
            {
                var end = m.AddMonths(1);
                var inMonth = mine.Where(t => t.Date >= m && t.Date < end).ToList();
                var startOwed = OwedAt(m.AddDays(-1));
                var endOwed = OwedAt(end.AddDays(-1) < DateTime.Today ? end.AddDays(-1) : DateTime.Today);
                // Ignored purchases and refunds are left out; balances owed are still the real ones.
                var purchases = -inMonth.Where(t => t.Amount < 0 && t.CategoryId != transfer && t.CategoryId != ignore).Sum(t => t.Amount);
                var advances = -inMonth.Where(t => t.Amount < 0 && t.CategoryId == transfer).Sum(t => t.Amount);
                var payments = inMonth.Where(t => t.Amount > 0 && t.CategoryId == transfer).Sum(t => t.Amount);
                var credits = inMonth.Where(t => t.Amount > 0 && t.CategoryId != transfer && t.CategoryId != ignore).Sum(t => t.Amount);
                // The month is only as good as its weakest payment.
                var statuses = payList.Where(p => p.Date >= m && p.Date < end).Select(p => p.Status).ToList();
                var status = statuses.Count == 0 ? (startOwed > 0.5m ? PayStatus.NoPayment : PayStatus.NothingOwed)
                    : statuses.Contains(PayStatus.PaidDown) ? PayStatus.PaidDown
                    : statuses.Contains(PayStatus.Unknown) ? PayStatus.Unknown
                    : statuses.Contains(PayStatus.InFull) ? PayStatus.InFull
                    : PayStatus.PaidOff;
                months.Add(new CardMonth(m, startOwed, purchases, advances, payments, credits, endOwed, status));
            }
            cards.Add(new CardReport(card, OwedAt(DateTime.Today), months, payList));
        }

        var cardPayment = cats.GetValueOrDefault(DbSeed.CardPayment);
        var unlinked = tx.Where(t => t.CategoryId == cardPayment && cardPayment != 0)
            .OrderByDescending(t => t.Date)
            .Select(t => new UnlinkedCardPayment(t.Date, t.Merchant ?? t.Description, -t.Amount, names.GetValueOrDefault(t.AccountId, "")))
            .ToList();
        return (cards, unlinked);
    }

    /// Works out what a card payment paid, without statement data: a statement-balance autopay
    /// equals, to the cent, what was owed on some day 10–40 days earlier (the closing date).
    private static (string Status, decimal? Statement, DateTime? StatementDate) ClassifyPayment(
        DateTime paid, decimal amount, Func<DateTime, decimal> owedAt, DateTime? historyStart)
    {
        var owedBefore = owedAt(paid.AddDays(-1));
        if (owedBefore > 0 && amount >= owedBefore - 0.005m) return (PayStatus.PaidOff, owedBefore, paid.AddDays(-1));
        for (var k = 10; k <= 40; k++)
        {
            var day = paid.AddDays(-k);
            if (historyStart is null || day < historyStart) break;
            if (Math.Abs(owedAt(day) - amount) < 0.01m) return (PayStatus.InFull, amount, day);
        }
        return historyStart is null || paid.AddDays(-40) < historyStart
            ? (PayStatus.Unknown, null, null)
            : (PayStatus.PaidDown, null, null);
    }

    // SQLite can't SUM decimals in SQL, so amounts are aggregated in memory.
    private static async Task<List<Tx>> LoadTxAsync(MoneyContext db, DateTime from, DateTime toExclusive) =>
        await db.Transactions.AsNoTracking()
            .Where(t => t.Date >= from && t.Date < toExclusive)
            .Select(t => new Tx(t.AccountId, t.Date, t.Amount, t.CategoryId))
            .ToListAsync();

    /// The Ignore category's id (-1 if it's missing, which matches nothing).
    public static async Task<int> IgnoreIdAsync(MoneyContext db) =>
        await db.Categories.Where(c => c.Name == DbSeed.Ignore).Select(c => (int?)c.Id).FirstOrDefaultAsync() ?? -1;

    private static async Task<(List<Account>, Dictionary<int, Ledger>)> LoadLedgersAsync(MoneyContext db)
    {
        var accounts = await db.Accounts.AsNoTracking().OrderBy(a => a.Name).ToListAsync();
        var tx = await db.Transactions.AsNoTracking()
            .Select(t => new { t.AccountId, t.Date, t.Amount }).ToListAsync();
        var snaps = await db.BalanceSnapshots.AsNoTracking().ToListAsync();
        var txBy = tx.ToLookup(t => t.AccountId);
        var snapBy = snaps.ToLookup(s => s.AccountId);
        var ledgers = accounts.ToDictionary(a => a.Id, a => new Ledger(
            txBy[a.Id].Select(t => (t.Date.Date, t.Amount)),
            snapBy[a.Id].Select(s => (s.Date.Date, a.Type.IsLiability() ? -s.Balance : s.Balance))));
        return (accounts, ledgers);
    }

    /// One account's history: sorted transactions with running totals, plus known balances.
    private class Ledger
    {
        private readonly DateTime[] _dates;
        private readonly decimal[] _running;     // _running[i] = sum of amounts of transactions 0..i
        private readonly (DateTime Date, decimal Value)[] _snaps;

        public Ledger(IEnumerable<(DateTime Date, decimal Amount)> tx, IEnumerable<(DateTime Date, decimal Value)> snaps)
        {
            var sorted = tx.OrderBy(t => t.Date).ToArray();
            _dates = sorted.Select(t => t.Date).ToArray();
            _running = new decimal[sorted.Length];
            decimal sum = 0;
            for (var i = 0; i < sorted.Length; i++) _running[i] = sum += sorted[i].Amount;
            // Keep the last snapshot of each day.
            _snaps = snaps.GroupBy(s => s.Date).Select(g => g.Last()).OrderBy(s => s.Date).ToArray();
        }

        public DateTime? FirstTransaction => _dates.Length > 0 ? _dates[0] : null;

        public DateTime? FirstDate => new[] { _dates.FirstOrDefault(), _snaps.FirstOrDefault().Date }
            .Where(d => d != default).Select(d => (DateTime?)d).Min();

        public DateTime? LatestSnapshot => _snaps.Length > 0 ? _snaps[^1].Date : null;

        /// Sum of transactions dated on or before <paramref name="d"/>.
        private decimal Through(DateTime d)
        {
            var i = Array.BinarySearch(_dates, d);
            if (i < 0) i = ~i - 1;
            else while (i + 1 < _dates.Length && _dates[i + 1] == d) i++;
            return i >= 0 ? _running[i] : 0;
        }

        /// End-of-day value on <paramref name="d"/>. A snapshot is assumed to include that
        /// day's transactions.
        public decimal ValueAt(DateTime d)
        {
            if (_snaps.Length == 0) return Through(d);
            var anchor = _snaps.LastOrDefault(s => s.Date <= d);
            if (anchor == default) anchor = _snaps[0];
            return anchor.Value + Through(d) - Through(anchor.Date);
        }
    }
}
