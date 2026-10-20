using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using MoneySite.Data;

namespace MoneySite.Services;

/// Rule-based auto-categorizer. Order of precedence:
///   1. a category picked by hand for that one transaction (never overwritten),
///   2. a transfer between two linked accounts — a payment out of one account matched to
///      the same amount arriving in another within a week (e.g. checking → credit card),
///   3. a category set for the whole company (older per-company setting),
///   4. card payments Plaid flags: money into a credit card is paying it down → Transfer; a card
///      payment out of checking with no matching card means that card isn't linked, so the
///      payment stands in for its purchases → Credit Card Payment (counts as spending),
///   5. keyword rules, matched against the bank description and the company name; when several
///      match, the longest (most specific) keyword wins, then the rule higher in the list,
///   6. other payments/transfers Plaid flags: money out of a credit card (e.g. Alipay) is really
///      a purchase → Uncategorized; anything else (Zelle, deposits) → Transfer,
///   7. Plaid's own category,
///   8. Uncategorized.
public class Categorizer
{
    private readonly IDbContextFactory<MoneyContext> _dbf;
    public Categorizer(IDbContextFactory<MoneyContext> dbf) => _dbf = dbf;

    /// How far apart the two halves of a transfer can post (Citi credits a payment a day
    /// before the bank debits it; some banks take several days).
    private const int TransferWindowDays = 7;

    /// Categorizes transactions that have no category yet, plus every payment/transfer (its
    /// other half may only arrive in a later sync). With <paramref name="reapplyAll"/>,
    /// re-runs the rules on every transaction that wasn't categorized by hand.
    /// Returns how many transactions changed category.
    public async Task<int> CategorizeAsync(bool reapplyAll = false)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        var rules = await db.CategorizationRules.AsNoTracking().OrderBy(r => r.Order).ThenBy(r => r.Id).ToListAsync();
        var catIds = await db.Categories.AsNoTracking()
            .ToDictionaryAsync(c => c.Name, c => c.Id, StringComparer.OrdinalIgnoreCase);
        int? Cat(string name) => catIds.TryGetValue(name, out var id) ? id : null;
        var merchantCats = await db.Merchants.AsNoTracking().Where(m => m.CategoryId != null)
            .ToDictionaryAsync(m => m.Name, m => m.CategoryId!.Value);
        var liabilities = (await db.Accounts.AsNoTracking().Select(a => new { a.Id, a.Type }).ToListAsync())
            .Where(a => a.Type.IsLiability()).Select(a => a.Id).ToHashSet();

        // Every keyword, most specific first: longest wins, then the rule higher in the list.
        var keywords = rules
            .SelectMany((r, rank) => SplitKeywords(r.Keywords)
                .Select(k => (r.CategoryId, Length: k.Length, Rank: rank, Pattern: KeywordRegex(k))))
            .OrderByDescending(k => k.Length).ThenBy(k => k.Rank)
            .ToList();
        int? RuleFor(Transaction t)
        {
            foreach (var k in keywords)
                if (k.Pattern.IsMatch(t.Description) || (t.Merchant is not null && k.Pattern.IsMatch(t.Merchant)))
                    return k.CategoryId;
            return null;
        }

        var all = await db.Transactions.ToListAsync();
        var peers = MatchTransfers(all);

        int changed = 0;
        foreach (var t in all)
        {
            t.TransferPeerId = peers.TryGetValue(t.Id, out var peer) ? peer : null;
            if (t.CategoryManual) continue;
            var transferish = IsTransferOrCardPayment(t.PlaidCategory);
            if (!reapplyAll && t.CategoryId is not null && !transferish && t.TransferPeerId is null) continue;

            var onCard = liabilities.Contains(t.AccountId);
            int? id;
            if (t.TransferPeerId is not null) id = Cat(DbSeed.Transfer);
            else if (t.Merchant is not null && merchantCats.TryGetValue(t.Merchant, out var mc)) id = mc;
            else if (transferish && onCard && t.Amount > 0) id = Cat(DbSeed.Transfer);
            else if (transferish && !onCard && t.Amount < 0 && t.PlaidCategory == "LOAN_PAYMENTS_CREDIT_CARD_PAYMENT")
                id = Cat(DbSeed.CardPayment);
            else if (RuleFor(t) is int ruleId) id = ruleId;
            else if (transferish) id = onCard ? null : Cat(DbSeed.Transfer);
            else id = FromPlaid(t.PlaidCategory, catIds);
            id ??= Cat(DbSeed.Uncategorized);
            if (t.CategoryId != id) { t.CategoryId = id; changed++; }
        }
        await db.SaveChangesAsync();
        return changed;
    }

    public static IEnumerable<string> SplitKeywords(string keywords) =>
        keywords.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// The keyword used for a company: its name, lowercased.
    public static string KeywordFor(string name) => name.Trim().Replace(";", " ").ToLowerInvariant();

    /// keyword → category id, for every keyword in the rules (first rule wins on duplicates).
    public async Task<Dictionary<string, int>> KeywordCategoriesAsync()
    {
        await using var db = await _dbf.CreateDbContextAsync();
        var rules = await db.CategorizationRules.AsNoTracking().OrderBy(r => r.Order).ThenBy(r => r.Id).ToListAsync();
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in rules)
            foreach (var k in SplitKeywords(r.Keywords))
                map.TryAdd(k, r.CategoryId);
        return map;
    }

    /// Adds a keyword to a category's keyword list (taking it out of any other category's list),
    /// then re-categorizes everything. With <paramref name="merchant"/>, also drops that company's
    /// older per-company category so the keyword takes effect. Returns how many changed.
    public async Task<int> AddKeywordAsync(string keyword, int categoryId, string? merchant = null)
    {
        keyword = KeywordFor(keyword);
        if (keyword.Length == 0) return 0;
        await using (var db = await _dbf.CreateDbContextAsync())
        {
            AddKeyword(db, await db.CategorizationRules.OrderBy(r => r.Order).ThenBy(r => r.Id).ToListAsync(), keyword, categoryId);
            await db.SaveChangesAsync();
            if (merchant is not null)
                await db.Merchants.Where(m => m.Name == merchant).ExecuteUpdateAsync(s => s.SetProperty(m => m.CategoryId, (int?)null));
        }
        return await CategorizeAsync(reapplyAll: true);
    }

    private static void AddKeyword(MoneyContext db, List<CategorizationRule> rules, string keyword, int categoryId)
    {
        RemoveFrom(db, rules.Where(r => r.CategoryId != categoryId), keyword);
        var target = rules.FirstOrDefault(r => r.CategoryId == categoryId && db.Entry(r).State != EntityState.Deleted);
        if (target is null)
        {
            target = new CategorizationRule
            {
                Keywords = keyword, CategoryId = categoryId, Order = rules.Count > 0 ? rules.Max(r => r.Order) + 1 : 1
            };
            db.CategorizationRules.Add(target);
            rules.Add(target);
        }
        else if (!SplitKeywords(target.Keywords).Contains(keyword, StringComparer.OrdinalIgnoreCase))
            target.Keywords = string.Join(";", SplitKeywords(target.Keywords).Append(keyword));
    }

    /// One-time: company categories set before keywords existed become keywords in that
    /// category, so every choice lives on the Categories page.
    public async Task MoveCompanySettingsToKeywordsAsync()
    {
        await using var db = await _dbf.CreateDbContextAsync();
        if (await db.AppSettings.FindAsync("CompanySettingsToKeywords") is not null) return;
        var rules = await db.CategorizationRules.OrderBy(r => r.Order).ThenBy(r => r.Id).ToListAsync();
        foreach (var m in await db.Merchants.Where(m => m.CategoryId != null).ToListAsync())
        {
            AddKeyword(db, rules, KeywordFor(m.Name), m.CategoryId!.Value);
            m.CategoryId = null;
        }
        db.AppSettings.Add(new AppSetting { Key = "CompanySettingsToKeywords", Value = "done" });
        await db.SaveChangesAsync();
    }

    private static void RemoveFrom(MoneyContext db, IEnumerable<CategorizationRule> rules, string keyword)
    {
        foreach (var r in rules.ToList())
        {
            var kept = SplitKeywords(r.Keywords).Where(k => !k.Equals(keyword, StringComparison.OrdinalIgnoreCase)).ToList();
            if (kept.Count == SplitKeywords(r.Keywords).Count()) continue;
            if (kept.Count == 0) db.CategorizationRules.Remove(r);
            else r.Keywords = string.Join(";", kept);
        }
    }

    /// Pairs each payment/transfer out of one account with the same amount arriving in a
    /// different account within <see cref="TransferWindowDays"/> days (closest date wins).
    /// Returns transaction id → the other half's id, both ways.
    public static Dictionary<int, int> MatchTransfers(IEnumerable<Transaction> transactions)
    {
        var list = transactions.Where(t => IsTransferOrCardPayment(t.PlaidCategory) || LooksLikePayment(t.Description)).ToList();
        var ins = list.Where(t => t.Amount > 0).ToLookup(t => t.Amount);
        var pairs = new Dictionary<int, int>();
        foreach (var o in list.Where(t => t.Amount < 0).OrderBy(t => t.Date).ThenBy(t => t.Id))
        {
            var match = ins[-o.Amount]
                .Where(i => i.AccountId != o.AccountId && !pairs.ContainsKey(i.Id)
                            && Math.Abs((i.Date - o.Date).TotalDays) <= TransferWindowDays)
                .OrderBy(i => Math.Abs((i.Date - o.Date).TotalDays)).ThenBy(i => i.Id)
                .FirstOrDefault();
            if (match is null) continue;
            pairs[o.Id] = match.Id;
            pairs[match.Id] = o.Id;
        }
        return pairs;
    }

    private static readonly Regex PaymentWords = new(@"\b(payment|pymt|autopay|transfer|advance)\b", RegexOptions.IgnoreCase);
    private static bool LooksLikePayment(string description) => PaymentWords.IsMatch(description);

    /// A keyword matches at the start of a word, so "rent" matches "Rent payment" and
    /// "rental" but not "current"; "fee" doesn't match "coffee".
    public static Regex KeywordRegex(string keyword) =>
        new(@"(?<![\p{L}\p{N}])" + Regex.Escape(keyword), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// Builds a starter keyword from a bank description, e.g. "STARBUCKS STORE 1234 SEATTLE" → "starbucks store".
    public static string SuggestKeyword(string description)
    {
        var words = Regex.Matches(description.ToLowerInvariant(), @"[\p{L}][\p{L}'&\.\-]*")
            .Select(m => m.Value.Trim('.', '-'))
            .Where(w => w.Length > 1)
            .Take(2);
        return string.Join(" ", words);
    }

    private static bool IsTransferOrCardPayment(string? plaid) =>
        plaid is not null && (plaid.StartsWith("TRANSFER_") || plaid == "LOAN_PAYMENTS_CREDIT_CARD_PAYMENT");

    /// Maps Plaid's personal_finance_category (detailed, e.g. FOOD_AND_DRINK_GROCERIES) onto our categories.
    private static int? FromPlaid(string? plaid, Dictionary<string, int> cats)
    {
        if (string.IsNullOrEmpty(plaid)) return null;
        string? name = plaid switch
        {
            "FOOD_AND_DRINK_GROCERIES" => "Groceries",
            _ when plaid.StartsWith("FOOD_AND_DRINK") => "Dining Out",
            _ when plaid.StartsWith("INCOME") => DbSeed.Income,
            _ when plaid.StartsWith("TRANSFER") => DbSeed.Transfer,
            "LOAN_PAYMENTS_CREDIT_CARD_PAYMENT" => DbSeed.Transfer,
            "LOAN_PAYMENTS_MORTGAGE_PAYMENT" => "Housing & Rent",
            "LOAN_PAYMENTS_CAR_PAYMENT" => "Car",
            // Other loan payments have no category of their own: set one per company on the Merchants page.
            _ when plaid.StartsWith("LOAN_PAYMENTS") => null,
            _ when plaid.StartsWith("BANK_FEES") => "Memberships",
            "PERSONAL_CARE_GYMS_AND_FITNESS_CENTERS" => "Memberships",
            _ when plaid.StartsWith("ENTERTAINMENT") => "Entertainment",
            _ when plaid.StartsWith("GENERAL_MERCHANDISE") => "Shopping",
            _ when plaid.StartsWith("HOME_IMPROVEMENT") => "Home Improvement",
            _ when plaid.StartsWith("MEDICAL") => "Health",
            _ when plaid.StartsWith("PERSONAL_CARE") => "Health",
            "GENERAL_SERVICES_AUTOMOTIVE" => "Car",
            _ when plaid.StartsWith("TRANSPORTATION") => "Car",
            _ when plaid.StartsWith("TRAVEL") => "Travel",
            "RENT_AND_UTILITIES_RENT" => "Housing & Rent",
            _ when plaid.StartsWith("RENT_AND_UTILITIES") => "Utilities",
            _ => null
        };
        return name is not null && cats.TryGetValue(name, out var id) ? id : null;
    }
}
