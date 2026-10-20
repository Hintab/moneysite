using Microsoft.EntityFrameworkCore;

// Schema helpers below build DDL from hard-coded table/column names only (identifiers can't be parameters).
#pragma warning disable EF1002

namespace MoneySite.Data;

/// Startup database setup. Every method is idempotent and safe to run on each start.
public static class DbSeed
{
    public static readonly (string Name, string Color)[] DefaultCategories =
    {
        ("Groceries",       "#2ecc71"),
        ("Dining Out",      "#e67e22"),
        ("Housing & Rent",  "#8e44ad"),
        ("Utilities",       "#16a085"),
        ("Car",             "#3498db"),
        ("Memberships",     "#f39c12"),
        ("Shopping",        "#9b59b6"),
        ("Health",          "#c0392b"),
        ("Entertainment",   "#2980b9"),
        ("Travel",          "#1abc9c"),
        ("Income",          "#27ae60"),
        ("Transfer",        "#7f8c8d"),
        ("Uncategorized",   "#bdc3c7"),
        ("Credit Card Payment", "#5d6d7e"),
        ("Home Improvement", "#a0522d"),
        ("Ignore",          "#566573"),
    };

    private const string CarKeywords =
        "shell;exxon;chevron;buc-ee;quicktrip;speedway;marathon;fuel station;parking;autozone;o'reilly;advance auto;jiffy lube;valvoline;car wash;tesla;bmv";
    private const string MembershipKeywords =
        "membership;annual fee;netflix;spotify;hulu;disney;icloud;apple.com/bill;youtube premium;planet fitness;ymca";

    /// Categories dropped in the October 2026 revision, and where their transactions, rules
    /// and company settings go (null = let the automatic rules decide again).
    private static readonly (string Old, string? New)[] Retired =
    {
        ("Fuel", "Car"), ("Transportation", "Car"), ("Subscriptions", "Memberships"), ("Loan Payments", null),
    };

    private const string HomeImprovementKeywords =
        "home depot;lowe's;lowes;menards;harbor freight;floor & decor;floor and decor;ace hardware;true value;sherwin-williams;sherwin williams";

    /// Names with special meaning in reports: Transfer and Income never count as spending, and
    /// Ignore is left out of every report (the transactions still show and still move balances).
    public const string Transfer = "Transfer", Income = "Income", Uncategorized = "Uncategorized", Ignore = "Ignore";

    /// A payment from checking to a card that isn't linked: the purchases on that card aren't
    /// visible, so the payment itself counts as spending.
    public const string CardPayment = "Credit Card Payment";

    public static void EnsureSeed(MoneyContext db)
    {
        if (db.Categories.Any() && db.AppSettings.Find("CategoryRevision") is null)
        {
            ReviseCategories(db);
            db.AppSettings.Add(new AppSetting { Key = "CategoryRevision", Value = "2026-10" });
            db.SaveChanges();
        }

        var have = db.Categories.Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, color) in DefaultCategories)
            if (!have.Contains(name)) db.Categories.Add(new Category { Name = name, Color = color });
        db.SaveChanges();

        if (db.CategorizationRules.Any())
        {
            // Runs once, even if a Home Improvement category was already added by hand.
            if (db.AppSettings.Find("HomeImprovementRules") is null)
            {
                MoveHomeStoresToHomeImprovement(db);
                db.AppSettings.Add(new AppSetting { Key = "HomeImprovementRules", Value = "added" });
                db.SaveChanges();
            }
            return;
        }

        int Id(string name) => db.Categories.AsNoTracking().Single(c => c.Name == name).Id;

        (int order, string keywords, string cat)[] rules =
        {
            (1,  "whole foods;grocery;grocer;kroger;safeway;trader joe;sprouts;aldi;food lion;meijer;costco;walmart;target", "Groceries"),
            (2,  "starbucks;chipotle;mcdonald;restaurant;doordash;ubereats;uber eats;grubhub;pizza;burger;cafe;sushi;brewpub;wings", "Dining Out"),
            (3,  CarKeywords, "Car"),
            (5,  "comcast;xfinity;verizon;at&t;t-mobile;electric;water bill;utility;internet", "Utilities"),
            (6,  "rent;landlord;property management;mortgage", "Housing & Rent"),
            (7,  MembershipKeywords, "Memberships"),
            (8,  "amazon;amzn;best buy;ebay", "Shopping"),
            (8,  HomeImprovementKeywords, "Home Improvement"),
            (9,  "cvs;walgreens;pharmacy;urgent care;doctor;dental", "Health"),
            (10, "ticketmaster;cinemark;amc theatres;gamestop;steam", "Entertainment"),
            (11, "airbnb;expedia;hotel;marriott;hyatt;hilton;airline;delta air;united airlines;american airlines;southwest", "Travel"),
            (12, "payroll;direct deposit;direct dep", "Income"),
            (13, "transfer;payment thank you;autopay;credit card payment;online payment", "Transfer"),
        };
        foreach (var (order, keywords, cat) in rules)
            db.CategorizationRules.Add(new CategorizationRule { Order = order, Keywords = keywords, CategoryId = Id(cat) });
        db.AppSettings.Add(new AppSetting { Key = "HomeImprovementRules", Value = "added" });
        db.AppSettings.Add(new AppSetting { Key = "CategoryRevision", Value = "2026-10" });

        db.SaveChanges();
    }

    /// One-time upgrade of an existing database to the current category list:
    /// "Fees &amp; Interest" becomes "Memberships", "Car" is added, and Fuel, Transportation,
    /// Subscriptions and Loan Payments are removed. Their rules and company settings move to the
    /// replacement category; hand-picked transaction categories move too (or go back to automatic).
    private static void ReviseCategories(MoneyContext db)
    {
        var cats = db.Categories.ToList();
        Category? Find(string name) => cats.FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

        var memberships = Find("Memberships");
        var fees = Find("Fees & Interest");
        if (memberships is null && fees is not null) { fees.Name = "Memberships"; memberships = fees; }
        memberships ??= db.Categories.Add(new Category { Name = "Memberships", Color = "#f39c12" }).Entity;
        var car = Find("Car") ?? db.Categories.Add(new Category { Name = "Car", Color = "#3498db" }).Entity;
        db.SaveChanges();
        var targets = new Dictionary<string, int> { ["Car"] = car.Id, ["Memberships"] = memberships.Id };

        foreach (var (oldName, newName) in Retired)
        {
            var old = Find(oldName);
            if (old is null) continue;
            int? to = newName is null ? null : targets[newName];

            foreach (var r in db.CategorizationRules.Where(r => r.CategoryId == old.Id).ToList())
            {
                if (to is int t) r.CategoryId = t;
                else db.CategorizationRules.Remove(r);
            }
            foreach (var m in db.Merchants.Where(m => m.CategoryId == old.Id).ToList()) m.CategoryId = to;
            foreach (var t in db.Transactions.Where(t => t.CategoryId == old.Id).ToList())
            {
                // Automatic ones are recategorized at startup; hand-picked ones follow the rename.
                t.CategoryId = t.CategoryManual ? to : null;
                if (to is null) t.CategoryManual = false;
            }
            db.SaveChanges();
            db.Categories.Remove(old);
            db.SaveChanges();
        }

        // Fuel and Transportation both became Car; merge their keyword rules into one.
        var carRules = db.CategorizationRules.Where(r => r.CategoryId == car.Id).OrderBy(r => r.Order).ToList();
        if (carRules.Count > 1)
        {
            carRules[0].Keywords = string.Join(";", carRules
                .SelectMany(r => r.Keywords.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                .Distinct(StringComparer.OrdinalIgnoreCase));
            db.CategorizationRules.RemoveRange(carRules.Skip(1));
        }
        db.SaveChanges();
    }

    /// One-time upgrade when Home Improvement is added to an existing database: home-store
    /// keywords move out of whatever rule had them (Shopping) into a new Home Improvement rule.
    private static void MoveHomeStoresToHomeImprovement(MoneyContext db)
    {
        var homeId = db.Categories.Single(c => c.Name == "Home Improvement").Id;
        var stores = HomeImprovementKeywords.Split(';').ToHashSet(StringComparer.OrdinalIgnoreCase);
        var rules = db.CategorizationRules.ToList();
        foreach (var r in rules)
        {
            var kept = r.Keywords.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(k => !stores.Contains(k)).ToList();
            if (kept.Count == 0) db.CategorizationRules.Remove(r);
            else r.Keywords = string.Join(";", kept);
        }
        var order = rules.FirstOrDefault(r => r.Keywords.Contains("amazon"))?.Order ?? rules.Select(r => r.Order).DefaultIfEmpty(1).Max();
        db.CategorizationRules.Add(new CategorizationRule { Keywords = HomeImprovementKeywords, CategoryId = homeId, Order = order });
        db.SaveChanges();
    }

    /// Brings an older SQLite file up to the current model. EnsureCreated() only builds
    /// brand-new databases, so new tables/columns/indexes are added here.
    public static void EnsureSchema(MoneyContext db)
    {
        db.Database.ExecuteSqlRaw(
            @"CREATE TABLE IF NOT EXISTS ""PlaidItems"" (
                 ""Id"" INTEGER NOT NULL CONSTRAINT ""PK_PlaidItems"" PRIMARY KEY AUTOINCREMENT,
                 ""PlaidItemId"" TEXT NOT NULL,
                 ""AccessToken"" TEXT NOT NULL,
                 ""Name"" TEXT NOT NULL,
                 ""Institution"" TEXT NOT NULL,
                 ""SyncCursor"" TEXT NOT NULL,
                 ""CreatedAt"" TEXT NOT NULL)");
        db.Database.ExecuteSqlRaw(
            @"CREATE TABLE IF NOT EXISTS ""BalanceSnapshots"" (
                 ""Id"" INTEGER NOT NULL CONSTRAINT ""PK_BalanceSnapshots"" PRIMARY KEY AUTOINCREMENT,
                 ""AccountId"" INTEGER NOT NULL,
                 ""Date"" TEXT NOT NULL,
                 ""Balance"" TEXT NOT NULL,
                 CONSTRAINT ""FK_BalanceSnapshots_Accounts_AccountId"" FOREIGN KEY (""AccountId"") REFERENCES ""Accounts"" (""Id"") ON DELETE CASCADE)");

        AddColumn(db, "Accounts", "PlaidAccountId", "TEXT");
        AddColumn(db, "Accounts", "PlaidItemId", "INTEGER");
        AddColumn(db, "Transactions", "PlaidTransactionId", "TEXT");
        AddColumn(db, "Transactions", "PlaidCategory", "TEXT");
        AddColumn(db, "Transactions", "CategoryManual", "INTEGER NOT NULL DEFAULT 0");
        AddColumn(db, "PlaidItems", "LastSyncedAt", "TEXT");
        AddColumn(db, "PlaidItems", "LastError", "TEXT");

        db.Database.ExecuteSqlRaw(
            @"CREATE TABLE IF NOT EXISTS ""Merchants"" (
                 ""Id"" INTEGER NOT NULL CONSTRAINT ""PK_Merchants"" PRIMARY KEY AUTOINCREMENT,
                 ""Name"" TEXT NOT NULL,
                 ""CategoryId"" INTEGER NULL,
                 CONSTRAINT ""FK_Merchants_Categories_CategoryId"" FOREIGN KEY (""CategoryId"") REFERENCES ""Categories"" (""Id"") ON DELETE SET NULL)");
        db.Database.ExecuteSqlRaw(@"CREATE UNIQUE INDEX IF NOT EXISTS ""IX_Merchants_Name"" ON ""Merchants"" (""Name"")");
        db.Database.ExecuteSqlRaw(
            @"CREATE TABLE IF NOT EXISTS ""MerchantAliases"" (
                 ""Id"" INTEGER NOT NULL CONSTRAINT ""PK_MerchantAliases"" PRIMARY KEY AUTOINCREMENT,
                 ""Raw"" TEXT NOT NULL,
                 ""Name"" TEXT NOT NULL)");
        db.Database.ExecuteSqlRaw(@"CREATE UNIQUE INDEX IF NOT EXISTS ""IX_MerchantAliases_Raw"" ON ""MerchantAliases"" (""Raw"")");

        db.Database.ExecuteSqlRaw(
            @"CREATE TABLE IF NOT EXISTS ""AppSettings"" (
                 ""Key"" TEXT NOT NULL CONSTRAINT ""PK_AppSettings"" PRIMARY KEY,
                 ""Value"" TEXT NOT NULL)");

        // Merchant grouping is new: re-download history once so existing transactions
        // get Plaid's clean company names (matched by transaction id, nothing is duplicated).
        if (AddColumn(db, "Transactions", "MerchantRaw", "TEXT"))
            db.Database.ExecuteSqlRaw(@"UPDATE ""PlaidItems"" SET ""SyncCursor"" = ''");
        AddColumn(db, "Transactions", "Merchant", "TEXT");
        AddColumn(db, "Transactions", "TransferPeerId", "INTEGER");
        AddColumn(db, "PlaidItems", "InvestmentsStatus", "TEXT");
        AddColumn(db, "Accounts", "TypeSetByUser", "INTEGER NOT NULL DEFAULT 0");

        db.Database.ExecuteSqlRaw(
            @"CREATE TABLE IF NOT EXISTS ""Securities"" (
                 ""Id"" INTEGER NOT NULL CONSTRAINT ""PK_Securities"" PRIMARY KEY AUTOINCREMENT,
                 ""PlaidSecurityId"" TEXT NOT NULL,
                 ""Name"" TEXT NOT NULL,
                 ""Ticker"" TEXT NULL,
                 ""Type"" TEXT NULL,
                 ""ClosePrice"" TEXT NULL)");
        db.Database.ExecuteSqlRaw(@"CREATE UNIQUE INDEX IF NOT EXISTS ""IX_Securities_PlaidSecurityId"" ON ""Securities"" (""PlaidSecurityId"")");
        db.Database.ExecuteSqlRaw(
            @"CREATE TABLE IF NOT EXISTS ""Holdings"" (
                 ""Id"" INTEGER NOT NULL CONSTRAINT ""PK_Holdings"" PRIMARY KEY AUTOINCREMENT,
                 ""AccountId"" INTEGER NOT NULL,
                 ""SecurityId"" INTEGER NOT NULL,
                 ""Quantity"" TEXT NOT NULL,
                 ""Price"" TEXT NOT NULL,
                 ""Value"" TEXT NOT NULL,
                 ""CostBasis"" TEXT NULL,
                 ""AsOf"" TEXT NOT NULL,
                 CONSTRAINT ""FK_Holdings_Accounts_AccountId"" FOREIGN KEY (""AccountId"") REFERENCES ""Accounts"" (""Id"") ON DELETE CASCADE,
                 CONSTRAINT ""FK_Holdings_Securities_SecurityId"" FOREIGN KEY (""SecurityId"") REFERENCES ""Securities"" (""Id"") ON DELETE CASCADE)");
        db.Database.ExecuteSqlRaw(
            @"CREATE TABLE IF NOT EXISTS ""InvestmentTransactions"" (
                 ""Id"" INTEGER NOT NULL CONSTRAINT ""PK_InvestmentTransactions"" PRIMARY KEY AUTOINCREMENT,
                 ""PlaidInvestmentTransactionId"" TEXT NOT NULL,
                 ""AccountId"" INTEGER NOT NULL,
                 ""SecurityId"" INTEGER NULL,
                 ""Date"" TEXT NOT NULL,
                 ""Name"" TEXT NOT NULL,
                 ""Type"" TEXT NOT NULL,
                 ""Subtype"" TEXT NOT NULL,
                 ""Quantity"" TEXT NOT NULL,
                 ""Price"" TEXT NOT NULL,
                 ""Amount"" TEXT NOT NULL,
                 ""Fees"" TEXT NOT NULL,
                 CONSTRAINT ""FK_InvestmentTransactions_Accounts_AccountId"" FOREIGN KEY (""AccountId"") REFERENCES ""Accounts"" (""Id"") ON DELETE CASCADE,
                 CONSTRAINT ""FK_InvestmentTransactions_Securities_SecurityId"" FOREIGN KEY (""SecurityId"") REFERENCES ""Securities"" (""Id"") ON DELETE SET NULL)");
        db.Database.ExecuteSqlRaw(@"CREATE UNIQUE INDEX IF NOT EXISTS ""IX_InvestmentTransactions_PlaidInvestmentTransactionId"" ON ""InvestmentTransactions"" (""PlaidInvestmentTransactionId"")");

        // Older databases had these as UNIQUE, which rejected legitimate duplicates.
        EnsureIndex(db, "IX_Accounts_Name", "Accounts", @"""Name""");
        EnsureIndex(db, "IX_Transactions_AccountId_Date_Description_Amount", "Transactions",
            @"""AccountId"", ""Date"", ""Description"", ""Amount""");
        EnsureIndex(db, "IX_Transactions_PlaidTransactionId", "Transactions", @"""PlaidTransactionId""");
        EnsureIndex(db, "IX_Transactions_Date", "Transactions", @"""Date""");
        EnsureIndex(db, "IX_Transactions_Merchant", "Transactions", @"""Merchant""");
    }

    /// Fills in merchant names for transactions that don't have one yet.
    public static void BackfillMerchants(MoneyContext db)
    {
        var missing = db.Transactions.Where(t => t.Merchant == null).ToList();
        if (missing.Count == 0) return;
        var aliases = db.MerchantAliases.AsNoTracking().ToDictionary(a => a.Raw, a => a.Name);
        foreach (var t in missing)
        {
            t.MerchantRaw ??= MoneySite.Services.MerchantNames.Clean(t.Description);
            t.Merchant = aliases.GetValueOrDefault(t.MerchantRaw, t.MerchantRaw);
        }
        db.SaveChanges();
    }

    private static void EnsureIndex(MoneyContext db, string name, string table, string columns)
    {
        var sql = Scalar(db, $"SELECT sql FROM sqlite_master WHERE type = 'index' AND name = '{name}'");
        if (sql is not null && !sql.Contains("UNIQUE", StringComparison.OrdinalIgnoreCase)) return;
        if (sql is not null) db.Database.ExecuteSqlRaw($@"DROP INDEX ""{name}""");
        db.Database.ExecuteSqlRaw($@"CREATE INDEX ""{name}"" ON ""{table}"" ({columns})");
    }

    /// Returns true when the column was added.
    private static bool AddColumn(MoneyContext db, string table, string column, string type)
    {
        if (Columns(db, table).Contains(column)) return false;
        db.Database.ExecuteSqlRaw($@"ALTER TABLE ""{table}"" ADD COLUMN ""{column}"" {type}");
        return true;
    }

    private static HashSet<string> Columns(MoneyContext db, string table)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        WithCommand(db, $"PRAGMA table_info(\"{table}\")", r =>
        {
            while (r.Read()) set.Add(r.GetValue(1).ToString()!);
        });
        return set;
    }

    private static string? Scalar(MoneyContext db, string sql)
    {
        string? result = null;
        WithCommand(db, sql, r => { if (r.Read() && !r.IsDBNull(0)) result = r.GetString(0); });
        return result;
    }

    private static void WithCommand(MoneyContext db, string sql, Action<System.Data.Common.DbDataReader> read)
    {
        var conn = db.Database.GetDbConnection();
        var wasOpen = conn.State == System.Data.ConnectionState.Open;
        if (!wasOpen) conn.Open();
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            using var r = cmd.ExecuteReader();
            read(r);
        }
        finally
        {
            if (!wasOpen) conn.Close();
        }
    }
}
