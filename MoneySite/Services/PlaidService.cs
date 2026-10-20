using System.Text;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using MoneySite.Data;

namespace MoneySite.Services;

public record PlaidAccount(string AccountId, string Name, string Type, string Subtype, decimal? Balance, string Mask);
public record PlaidTransaction(string TransactionId, string AccountId, DateTime Date, string Name, decimal Amount, string? Category, string? MerchantName);
public record SyncPage(List<PlaidTransaction> Added, List<PlaidTransaction> Modified, List<string> Removed, string NextCursor, bool HasMore, string UpdateStatus);
/// SkippedAccounts = accounts not added because the account limit was reached.
public record PullResult(int Added, int Updated, int Removed, int SkippedAccounts = 0);

public class PlaidException : Exception
{
    public string? ErrorCode { get; }
    public PlaidException(string message, string? errorCode) : base(message) => ErrorCode = errorCode;
}

/// Thin client over the Plaid REST API (sandbox or production, chosen by config).
/// Plaid authenticates with client_id + secret in each JSON body — no header auth.
public class PlaidService
{
    private readonly HttpClient _http;
    private readonly IDbContextFactory<MoneyContext> _dbf;
    private readonly Categorizer _categorizer;
    private readonly ILogger<PlaidService> _log;

    // One pull at a time across the app, so a background sync and a "Sync now" click
    // can't both insert the same transactions.
    private static readonly SemaphoreSlim PullLock = new(1, 1);

    public string ClientId { get; }
    public string Secret { get; }
    public string EnvironmentName { get; }      // "sandbox" | "production" ("trial" is accepted as production)
    public string Host { get; }
    public bool IsConfigured => !string.IsNullOrWhiteSpace(ClientId) && !string.IsNullOrWhiteSpace(Secret);

    /// Most accounts that can be connected at once (config Plaid:MaxAccounts, default 10).
    public int MaxAccounts { get; }

    /// How much history to request when a bank is first linked (Plaid max is 730).
    public const int HistoryDays = 730;

    public PlaidService(HttpClient http, IConfiguration cfg, IDbContextFactory<MoneyContext> dbf,
        Categorizer categorizer, ILogger<PlaidService> log)
    {
        _http = http;
        _dbf = dbf;
        _categorizer = categorizer;
        _log = log;
        var p = cfg.GetSection("Plaid");
        ClientId = (p["ClientID"] ?? "").Trim();
        Secret = (p["ClientSecret"] ?? p["Secret"] ?? "").Trim();
        MaxAccounts = int.TryParse(p["MaxAccounts"], out var max) && max > 0 ? max : 10;
        EnvironmentName = (p["Environment"] ?? "sandbox").Trim().ToLowerInvariant();
        // Plaid's free Trial plan uses the Production environment (real banks, limited connections).
        if (EnvironmentName == "trial") EnvironmentName = "production";
        Host = EnvironmentName == "production" ? "https://production.plaid.com" : "https://sandbox.plaid.com";
    }

    private async Task<JsonNode> CallAsync(string path, JsonObject body, CancellationToken ct = default)
    {
        body["client_id"] = ClientId;
        body["secret"] = Secret;
        using var content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var resp = await _http.PostAsync($"{Host}/{path}", content, ct);
        var text = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
        {
            // Plaid errors are JSON: { error_code, error_message, display_message, ... }
            string? code = null, message = null;
            try
            {
                var err = JsonNode.Parse(text);
                code = err?["error_code"]?.GetValue<string>();
                message = err?["display_message"]?.GetValue<string>() ?? err?["error_message"]?.GetValue<string>();
            }
            catch { /* not JSON */ }
            throw new PlaidException($"Plaid /{path} failed: {code ?? $"HTTP {(int)resp.StatusCode}"} — {message ?? Truncate(text, 300)}", code);
        }
        return JsonNode.Parse(text) ?? new JsonObject();
    }

    /// 1. Server mints a one-time link_token the browser uses to open Plaid Link.
    /// Investments is requested as optional, so retirement/brokerage accounts get holdings and
    /// activity while plain banks still link normally. If this Plaid plan doesn't include
    /// Investments, the token is made without it.
    public async Task<string> CreateLinkTokenAsync()
    {
        JsonObject Body(bool withInvestments)
        {
            var body = new JsonObject
            {
                ["user"] = new JsonObject { ["client_user_id"] = "money-site-local" },
                ["client_name"] = "Money Manager",
                ["products"] = new JsonArray("transactions"),
                ["transactions"] = new JsonObject { ["days_requested"] = HistoryDays },
                ["country_codes"] = new JsonArray("US"),
                ["language"] = "en"
            };
            if (withInvestments) body["optional_products"] = new JsonArray("investments");
            return body;
        }
        JsonNode res;
        try { res = await CallAsync("link/token/create", Body(true)); }
        catch (PlaidException ex) when (ex.ErrorCode is "INVALID_PRODUCT" or "INVALID_FIELD" or "PRODUCTS_NOT_SUPPORTED")
        {
            res = await CallAsync("link/token/create", Body(false));
        }
        return LinkToken(res);
    }

    /// A link_token for "update mode": re-opens Plaid for an existing connection so the user can
    /// approve Investments access (holdings and activity) for it. No new connection is made.
    public async Task<string> CreateInvestmentsConsentTokenAsync(string accessToken)
    {
        var res = await CallAsync("link/token/create", new JsonObject
        {
            ["user"] = new JsonObject { ["client_user_id"] = "money-site-local" },
            ["client_name"] = "Money Manager",
            ["access_token"] = accessToken,
            ["additional_consented_products"] = new JsonArray("investments"),
            ["country_codes"] = new JsonArray("US"),
            ["language"] = "en"
        });
        return LinkToken(res);
    }

    private static string LinkToken(JsonNode res)
    {
        var token = res["link_token"]?.GetValue<string>();
        if (string.IsNullOrEmpty(token))
            throw new PlaidException("Plaid /link/token/create returned no link_token.", null);
        return token;
    }

    /// 2. Exchange the public_token Link returned for a long-lived access_token.
    public async Task<(string AccessToken, string ItemId)> ExchangePublicTokenAsync(string publicToken)
    {
        var res = await CallAsync("item/public_token/exchange", new JsonObject { ["public_token"] = publicToken });
        return (Str(res["access_token"]) ?? "", Str(res["item_id"]) ?? "");
    }

    /// Revokes the access_token at Plaid (used when disconnecting a bank).
    public Task RemoveItemAsync(string accessToken) =>
        CallAsync("item/remove", new JsonObject { ["access_token"] = accessToken });

    /// 3. Accounts, balances, and institution name for an access_token.
    public async Task<(List<PlaidAccount> Accounts, string InstitutionName)> GetAccountsAsync(string accessToken, CancellationToken ct = default)
    {
        var res = await CallAsync("accounts/get", new JsonObject { ["access_token"] = accessToken }, ct);
        var list = new List<PlaidAccount>();
        foreach (var a in (res["accounts"] as JsonArray) ?? new JsonArray())
        {
            if (a is null) continue;
            list.Add(new PlaidAccount(
                Str(a["account_id"]) ?? "",
                Str(a["name"]) ?? Str(a["official_name"]) ?? "",
                Str(a["type"]) ?? "other",
                Str(a["subtype"]) ?? "",
                ReadDecimal(a["balances"]?["current"]),
                Str(a["mask"]) ?? ""));
        }
        return (list, Str(res["item"]?["institution_name"]) ?? "");
    }

    /// 4. One page of incremental transaction changes (cursor-based; null cursor = from the start).
    public async Task<SyncPage> SyncPageAsync(string accessToken, string? cursor, CancellationToken ct = default)
    {
        var body = new JsonObject
        {
            ["access_token"] = accessToken,
            ["count"] = 500,
            ["options"] = new JsonObject { ["include_original_description"] = false }
        };
        if (!string.IsNullOrEmpty(cursor)) body["cursor"] = cursor;
        var res = await CallAsync("transactions/sync", body, ct);

        List<PlaidTransaction> Read(string key)
        {
            var list = new List<PlaidTransaction>();
            foreach (var t in (res[key] as JsonArray) ?? new JsonArray())
            {
                if (t is null || t["pending"]?.GetValue<bool>() == true) continue;   // posted transactions only
                var dateStr = Str(t["date"]);
                if (dateStr is null || !DateTime.TryParse(dateStr, out var date)) continue;
                list.Add(new PlaidTransaction(
                    Str(t["transaction_id"]) ?? "",
                    Str(t["account_id"]) ?? "",
                    date.Date,
                    Str(t["name"]) ?? Str(t["merchant_name"]) ?? "(no name)",
                    // Plaid: positive = money out of the account. This app: negative = money out.
                    -(ReadDecimal(t["amount"]) ?? 0),
                    Str(t["personal_finance_category"]?["detailed"]) ?? Str(t["personal_finance_category"]?["primary"]),
                    Str(t["merchant_name"])));
            }
            return list;
        }

        var removed = ((res["removed"] as JsonArray) ?? new JsonArray())
            .Select(r => Str(r?["transaction_id"])).OfType<string>().ToList();
        return new SyncPage(Read("added"), Read("modified"), removed,
            Str(res["next_cursor"]) ?? cursor ?? "",
            res["has_more"]?.GetValue<bool>() == true,
            Str(res["transactions_update_status"]) ?? "");
    }

    /// Pages through /transactions/sync until has_more is false. On a brand-new item Plaid
    /// may still be preparing data (NOT_READY), so the first pull waits briefly for it.
    private async Task<(List<SyncPage> Pages, string Cursor)> SyncAllAsync(string accessToken, string? startCursor, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            var pages = new List<SyncPage>();
            var cursor = startCursor;
            try
            {
                SyncPage page;
                do
                {
                    page = await SyncPageAsync(accessToken, cursor, ct);
                    pages.Add(page);
                    cursor = page.NextCursor;
                } while (page.HasMore);

                var waitingForFirstData = string.IsNullOrEmpty(startCursor)
                    && page.UpdateStatus == "NOT_READY" && pages.All(p => p.Added.Count == 0);
                if (waitingForFirstData && attempt < 15)
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), ct);
                    continue;
                }
                return (pages, cursor ?? "");
            }
            catch (PlaidException ex) when (ex.ErrorCode == "TRANSACTIONS_SYNC_MUTATION_DURING_PAGINATION" && attempt < 3)
            {
                // Plaid's documented recovery: restart the whole pagination from the original cursor.
            }
        }
    }

    /// Pulls accounts (creating matching Account rows), records today's balances, applies
    /// new/changed/removed transactions, then auto-categorizes. Safe to call repeatedly.
    public async Task<PullResult> PullItemAsync(int plaidItemId, CancellationToken ct = default)
    {
        await PullLock.WaitAsync(ct);
        try
        {
            await using var db = await _dbf.CreateDbContextAsync(ct);
            var item = await db.PlaidItems.FirstAsync(p => p.Id == plaidItemId, ct);
            try
            {
                var result = await PullCoreAsync(db, item, ct);
                item.LastSyncedAt = DateTime.UtcNow;
                item.LastError = null;
                await db.SaveChangesAsync(ct);
                // Investments never fail the sync: a missing approval or unsupported account is just noted.
                var status = await PullInvestmentsAsync(db, item, ct);
                await db.PlaidItems.Where(p => p.Id == plaidItemId)
                    .ExecuteUpdateAsync(s => s.SetProperty(p => p.InvestmentsStatus, status), ct);
                await _categorizer.CategorizeAsync();
                return result;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                db.ChangeTracker.Clear();
                await db.PlaidItems.Where(p => p.Id == plaidItemId)
                    .ExecuteUpdateAsync(s => s.SetProperty(p => p.LastError, Truncate(ex.Message, 500)), ct);
                throw;
            }
        }
        finally
        {
            PullLock.Release();
        }
    }

    private async Task<PullResult> PullCoreAsync(MoneyContext db, PlaidItem item, CancellationToken ct)
    {
        var (accounts, institution) = await GetAccountsAsync(item.AccessToken, ct);
        if (!string.IsNullOrWhiteSpace(institution)) { item.Name = institution; item.Institution = institution; }

        var dbAccounts = (await db.Accounts.Where(a => a.PlaidItemId == item.Id && a.PlaidAccountId != null).ToListAsync(ct))
            .ToDictionary(a => a.PlaidAccountId!, StringComparer.Ordinal);

        // Connected accounts from other banks count toward the limit; this bank's existing ones are kept.
        var connectedElsewhere = await db.Accounts.CountAsync(a => a.PlaidItemId != null && a.PlaidItemId != item.Id, ct);
        var skipped = 0;
        foreach (var pa in accounts)
        {
            if (!dbAccounts.TryGetValue(pa.AccountId, out var acc))
            {
                if (connectedElsewhere + dbAccounts.Count >= MaxAccounts)
                {
                    skipped++;
                    continue;
                }
                var name = string.IsNullOrWhiteSpace(pa.Name) ? "Account" : pa.Name;
                acc = new Account
                {
                    Name = string.IsNullOrEmpty(pa.Mask) ? name : $"{name} ••{pa.Mask}",
                    Institution = item.Institution,
                    PlaidAccountId = pa.AccountId,
                    PlaidItemId = item.Id
                };
                db.Accounts.Add(acc);
                dbAccounts[pa.AccountId] = acc;
            }
            if (!acc.TypeSetByUser) acc.Type = MapType(pa.Type, pa.Subtype);
        }
        await db.SaveChangesAsync(ct);

        // One balance snapshot per account per day → drives the net-worth history.
        var today = DateTime.Today;
        var accIds = dbAccounts.Values.Select(a => a.Id).ToList();
        var todays = await db.BalanceSnapshots.Where(s => accIds.Contains(s.AccountId) && s.Date >= today).ToListAsync(ct);
        foreach (var pa in accounts)
        {
            if (pa.Balance is null || !dbAccounts.TryGetValue(pa.AccountId, out var acc)) continue;
            var snap = todays.FirstOrDefault(s => s.AccountId == acc.Id);
            if (snap is null) db.BalanceSnapshots.Add(new BalanceSnapshot { AccountId = acc.Id, Date = today, Balance = pa.Balance.Value });
            else snap.Balance = pa.Balance.Value;
        }

        var (pages, cursor) = await SyncAllAsync(item.AccessToken, string.IsNullOrEmpty(item.SyncCursor) ? null : item.SyncCursor, ct);

        var map = dbAccounts.ToDictionary(kv => kv.Key, kv => kv.Value.Id, StringComparer.Ordinal);
        var existing = (await db.Transactions.Where(t => t.PlaidTransactionId != null && accIds.Contains(t.AccountId))
                .ToListAsync(ct))
            .ToDictionary(t => t.PlaidTransactionId!, StringComparer.Ordinal);

        var aliases = await db.MerchantAliases.AsNoTracking().ToDictionaryAsync(a => a.Raw, a => a.Name, ct);

        int added = 0, updated = 0, removed = 0;
        foreach (var t in pages.SelectMany(p => p.Added.Concat(p.Modified)))
        {
            if (!map.TryGetValue(t.AccountId, out var accId)) continue;
            var merchantRaw = string.IsNullOrWhiteSpace(t.MerchantName) ? MerchantNames.Clean(t.Name) : t.MerchantName.Trim();
            var merchant = MerchantService.Resolve(merchantRaw, aliases);
            if (existing.TryGetValue(t.TransactionId, out var tx))
            {
                if (tx.Date != t.Date || tx.Amount != t.Amount || tx.Description != t.Name) updated++;
                tx.Date = t.Date;
                tx.Amount = t.Amount;
                tx.Description = t.Name;
                tx.Type = t.Amount < 0 ? "Debit" : "Credit";
                tx.PlaidCategory = t.Category;
                tx.MerchantRaw = merchantRaw;
                tx.Merchant = merchant;
                continue;
            }
            tx = new Transaction
            {
                AccountId = accId,
                Date = t.Date,
                Description = t.Name,
                Type = t.Amount < 0 ? "Debit" : "Credit",
                Amount = t.Amount,
                PlaidTransactionId = t.TransactionId,
                PlaidCategory = t.Category,
                MerchantRaw = merchantRaw,
                Merchant = merchant
            };
            db.Transactions.Add(tx);
            existing[t.TransactionId] = tx;
            added++;
        }
        foreach (var id in pages.SelectMany(p => p.Removed))
        {
            if (existing.Remove(id, out var tx)) { db.Transactions.Remove(tx); removed++; }
        }

        item.SyncCursor = cursor;
        await db.SaveChangesAsync(ct);
        _log.LogInformation("Plaid sync {Item}: +{Added} ~{Updated} -{Removed}", item.Name, added, updated, removed);
        if (skipped > 0)
            _log.LogInformation("Plaid sync {Item}: skipped {Skipped} account(s) over the {Max}-account limit", item.Name, skipped, MaxAccounts);
        return new PullResult(added, updated, removed, skipped);
    }

    /// Investment accounts only: current holdings (replaced each time) and up to two years of
    /// activity (contributions, dividends, buys, sells, fees). Returns null when everything came
    /// through, or why it couldn't: "consent" (needs the one-time approval), "preparing", or an error.
    private async Task<string?> PullInvestmentsAsync(MoneyContext db, PlaidItem item, CancellationToken ct)
    {
        var accounts = await db.Accounts.Where(a => a.PlaidItemId == item.Id && (a.Type == AccountType.Investment || a.Type == AccountType.Retirement) && a.PlaidAccountId != null)
            .ToListAsync(ct);
        if (accounts.Count == 0) return null;
        var byPlaidId = accounts.ToDictionary(a => a.PlaidAccountId!, StringComparer.Ordinal);
        var accIds = accounts.Select(a => a.Id).ToList();

        try
        {
            var holdingsRes = await CallAsync("investments/holdings/get", new JsonObject { ["access_token"] = item.AccessToken }, ct);
            var securities = await UpsertSecuritiesAsync(db, holdingsRes["securities"] as JsonArray, ct);

            await db.Holdings.Where(h => accIds.Contains(h.AccountId)).ExecuteDeleteAsync(ct);
            foreach (var h in (holdingsRes["holdings"] as JsonArray) ?? new JsonArray())
            {
                if (h is null || !byPlaidId.TryGetValue(Str(h["account_id"]) ?? "", out var acc)) continue;
                if (!securities.TryGetValue(Str(h["security_id"]) ?? "", out var sec)) continue;
                var asOf = Str(h["institution_price_as_of"]);
                db.Holdings.Add(new Holding
                {
                    AccountId = acc.Id,
                    SecurityId = sec.Id,
                    Quantity = ReadDecimal(h["quantity"]) ?? 0,
                    Price = ReadDecimal(h["institution_price"]) ?? 0,
                    Value = ReadDecimal(h["institution_value"]) ?? 0,
                    CostBasis = ReadDecimal(h["cost_basis"]),
                    AsOf = asOf is not null && DateTime.TryParse(asOf, out var d) ? d.Date : DateTime.Today
                });
            }
            await db.SaveChangesAsync(ct);

            // Activity: always ask for the full two years. Some institutions (Empower) fill in history
            // over several days; already-saved records are skipped by id, so re-asking is cheap.
            var start = DateTime.Today.AddDays(-HistoryDays);
            var existing = (await db.InvestmentTransactions.Where(t => accIds.Contains(t.AccountId))
                    .Select(t => t.PlaidInvestmentTransactionId).ToListAsync(ct))
                .ToHashSet(StringComparer.Ordinal);
            int offset = 0, total;
            do
            {
                var res = await CallAsync("investments/transactions/get", new JsonObject
                {
                    ["access_token"] = item.AccessToken,
                    ["start_date"] = start.ToString("yyyy-MM-dd"),
                    ["end_date"] = DateTime.Today.ToString("yyyy-MM-dd"),
                    ["options"] = new JsonObject { ["count"] = 500, ["offset"] = offset }
                }, ct);
                total = res["total_investment_transactions"]?.GetValue<int>() ?? 0;
                var page = (res["investment_transactions"] as JsonArray) ?? new JsonArray();
                securities = await UpsertSecuritiesAsync(db, res["securities"] as JsonArray, ct);
                foreach (var t in page)
                {
                    if (t is null) continue;
                    var id = Str(t["investment_transaction_id"]) ?? "";
                    if (id.Length == 0 || !existing.Add(id)) continue;
                    if (!byPlaidId.TryGetValue(Str(t["account_id"]) ?? "", out var acc)) continue;
                    var dateStr = Str(t["date"]);
                    if (dateStr is null || !DateTime.TryParse(dateStr, out var date)) continue;
                    db.InvestmentTransactions.Add(new InvestmentTransaction
                    {
                        PlaidInvestmentTransactionId = id,
                        AccountId = acc.Id,
                        SecurityId = securities.TryGetValue(Str(t["security_id"]) ?? "", out var sec) ? sec.Id : null,
                        Date = date.Date,
                        Name = Str(t["name"]) ?? "",
                        Type = Str(t["type"]) ?? "",
                        Subtype = Str(t["subtype"]) ?? "",
                        Quantity = ReadDecimal(t["quantity"]) ?? 0,
                        Price = ReadDecimal(t["price"]) ?? 0,
                        Amount = ReadDecimal(t["amount"]) ?? 0,
                        Fees = ReadDecimal(t["fees"]) ?? 0
                    });
                }
                offset += page.Count;
                if (page.Count == 0) break;
            } while (offset < total);
            await db.SaveChangesAsync(ct);
            return null;
        }
        catch (PlaidException ex) when (ex.ErrorCode is "ADDITIONAL_CONSENT_REQUIRED" or "PRODUCTS_NOT_SUPPORTED"
                                        or "INVALID_PRODUCT" or "PRODUCT_NOT_ENABLED" or "NO_INVESTMENT_AUTH_ACCESS")
        {
            db.ChangeTracker.Clear();
            _log.LogInformation("Investments not available for {Item}: {Code}", item.Name, ex.ErrorCode);
            return ex.ErrorCode == "ADDITIONAL_CONSENT_REQUIRED" ? "consent" : ex.Message;
        }
        catch (PlaidException ex) when (ex.ErrorCode == "PRODUCT_NOT_READY")
        {
            db.ChangeTracker.Clear();
            return "preparing";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            db.ChangeTracker.Clear();
            _log.LogWarning(ex, "Investments pull failed for {Item}", item.Name);
            return Truncate(ex.Message, 300);
        }
    }

    private static async Task<Dictionary<string, Security>> UpsertSecuritiesAsync(MoneyContext db, JsonArray? list, CancellationToken ct)
    {
        var map = (await db.Securities.ToListAsync(ct)).ToDictionary(s => s.PlaidSecurityId, StringComparer.Ordinal);
        foreach (var s in list ?? new JsonArray())
        {
            var id = Str(s?["security_id"]);
            if (s is null || id is null) continue;
            if (!map.TryGetValue(id, out var sec)) db.Securities.Add(map[id] = sec = new Security { PlaidSecurityId = id });
            sec.Name = Str(s["name"]) ?? sec.Name;
            sec.Ticker = Str(s["ticker_symbol"]) ?? sec.Ticker;
            sec.Type = Str(s["type"]) ?? sec.Type;
            sec.ClosePrice = ReadDecimal(s["close_price"]) ?? sec.ClosePrice;
        }
        await db.SaveChangesAsync(ct);
        return map;
    }

    // Plaid investment subtypes for retirement plans (401k, 403b, IRA, Roth, pension, …).
    private static bool IsRetirementSubtype(string subtype)
    {
        var s = subtype.ToLowerInvariant();
        return s.Contains("401") || s.Contains("403") || s.Contains("457") || s.Contains("ira") || s.Contains("roth")
               || s.Contains("retirement") || s.Contains("pension") || s.Contains("thrift") || s.Contains("keogh")
               || s.Contains("profit sharing") || s.Contains("rrsp") || s.Contains("tfsa");
    }

    private static AccountType MapType(string type, string subtype) => type.ToLowerInvariant() switch
    {
        "credit" => AccountType.CreditCard,
        "loan" => AccountType.Loan,
        "investment" or "brokerage" => IsRetirementSubtype(subtype) ? AccountType.Retirement : AccountType.Investment,
        "depository" => subtype.ToLowerInvariant() switch
        {
            "checking" => AccountType.Checking,
            "cd" => AccountType.CD,
            "savings" or "money market" or "hsa" or "cash management" => AccountType.Savings,
            _ => AccountType.Other
        },
        _ => AccountType.Other
    };

    private static string? Str(JsonNode? n)
    {
        try { return n?.GetValue<string>(); }
        catch { return null; }
    }

    private static decimal? ReadDecimal(JsonNode? n)
    {
        try { return n?.GetValue<decimal>(); }
        catch { return null; }
    }

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n] + "…";
}
