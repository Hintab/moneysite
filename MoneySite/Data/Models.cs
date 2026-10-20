namespace MoneySite.Data;

public class Account
{
    public int Id { get; set; }
    public string Name { get; set; } = "";          // e.g. "Everyday Checking"
    public string Institution { get; set; } = "";      // bank name reported by Plaid
    public AccountType Type { get; set; } = AccountType.Checking;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public List<Transaction> Transactions { get; set; } = new();
    public string? PlaidAccountId { get; set; }          // Plaid account id
    public int? PlaidItemId { get; set; }                // connected bank; null once disconnected
    public bool TypeSetByUser { get; set; }              // labeled on the Accounts page — syncs leave Type alone
}

// Stored as an int — only ever append new values.
public enum AccountType { Checking, Savings, CreditCard, Other, Loan, Investment, CD, Retirement }

public static class AccountTypeExtensions
{
    /// The labels offered on the Accounts page, in display order.
    public static readonly AccountType[] Choices =
    {
        AccountType.Checking, AccountType.Savings, AccountType.CD, AccountType.Retirement, AccountType.Investment, AccountType.CreditCard,
    };

    /// Liabilities (cards, loans) store their balance as the amount owed and count negative toward net worth.
    public static bool IsLiability(this AccountType t) => t is AccountType.CreditCard or AccountType.Loan;

    /// Accounts whose holdings and activity come from Plaid Investments.
    public static bool IsInvestment(this AccountType t) => t is AccountType.Investment or AccountType.Retirement;

    public static string Label(this AccountType t) => t switch
    {
        AccountType.CreditCard => "Credit card",
        _ => t.ToString()
    };
}

public class Transaction
{
    public int Id { get; set; }
    public DateTime Date { get; set; }
    public required string Description { get; set; }
    public string Type { get; set; } = "Debit";        // Credit / Debit (bank sign convention)
    public decimal Amount { get; set; }                // negatives = money out, positives = money in
    public int AccountId { get; set; }
    public Account? Account { get; set; }
    public int? CategoryId { get; set; }
    public Category? Category { get; set; }
    public bool CategoryManual { get; set; }           // set by hand — rules never overwrite it
    public string? PlaidTransactionId { get; set; }      // Plaid transaction id (Plaid-synced tx only)
    public string? PlaidCategory { get; set; }           // Plaid personal_finance_category.detailed, e.g. FOOD_AND_DRINK_GROCERIES
    public string? MerchantRaw { get; set; }             // Plaid merchant_name, or a cleaned-up description
    public string? Merchant { get; set; }                // company this is grouped under (MerchantRaw after combines/renames)
    public int? TransferPeerId { get; set; }             // the other half of a transfer between two linked accounts (e.g. card payment)
}

/// Per-company settings, keyed by the merchant name shown in the app.
public class Merchant
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public int? CategoryId { get; set; }                 // "always categorize this company as…"; null = automatic
    public Category? Category { get; set; }
}

/// Remembers that a raw merchant name was combined into / renamed to another, so future syncs follow it.
public class MerchantAlias
{
    public int Id { get; set; }
    public string Raw { get; set; } = "";
    public string Name { get; set; } = "";
}

public class Category
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Color { get; set; } = "#4f8ef7";
    public List<Transaction> Transactions { get; set; } = new();
}

public class CategorizationRule
{
    public int Id { get; set; }
    public string Keywords { get; set; } = "";      // semicolon-separated, case-insensitive
    public int Order { get; set; }                     // lower = checked first
    public int CategoryId { get; set; }
    public Category? Category { get; set; }
}

// An account balance on a date, recorded on every Plaid sync (one per account per day).
public class BalanceSnapshot
{
    public int Id { get; set; }
    public int AccountId { get; set; }
    public DateTime Date { get; set; }
    public decimal Balance { get; set; }               // for credit cards/loans: amount owed
    public Account? Account { get; set; }
}

// A bank login connected through Plaid. One item can own several Account rows.
public class PlaidItem
{
    public int Id { get; set; }
    public string PlaidItemId { get; set; } = "";     // Plaid's own item id
    public string AccessToken { get; set; } = "";     // used for all Plaid API calls for this item
    public string Name { get; set; } = "";            // display name (institution)
    public string Institution { get; set; } = "";
    public string SyncCursor { get; set; } = "";      // transactions/sync incremental cursor
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastSyncedAt { get; set; }
    public string? LastError { get; set; }
    /// null = fine (or no investment accounts); otherwise why investments can't be pulled yet:
    /// "consent" (needs the one-time approval in Plaid), "preparing", or an error message.
    public string? InvestmentsStatus { get; set; }
    public List<Account> Accounts { get; set; } = new();
}

/// Simple key/value app settings (e.g. whether reports are limited to the shared history window).
public class AppSetting
{
    public string Key { get; set; } = "";
    public string Value { get; set; } = "";
}

/// A fund or stock held in an investment account (from Plaid Investments).
public class Security
{
    public int Id { get; set; }
    public string PlaidSecurityId { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Ticker { get; set; }
    public string? Type { get; set; }                    // "mutual fund", "etf", "equity", "cash", …
    public decimal? ClosePrice { get; set; }
}

/// What an investment account holds right now. Replaced on every sync.
public class Holding
{
    public int Id { get; set; }
    public int AccountId { get; set; }
    public Account? Account { get; set; }
    public int SecurityId { get; set; }
    public Security? Security { get; set; }
    public decimal Quantity { get; set; }
    public decimal Price { get; set; }
    public decimal Value { get; set; }
    public decimal? CostBasis { get; set; }              // total cost, when the provider reports it
    public DateTime AsOf { get; set; }
}

/// Activity inside an investment account: contributions, dividends, buys, sells, fees.
/// Kept apart from Transactions so it never counts as spending or income.
public class InvestmentTransaction
{
    public int Id { get; set; }
    public string PlaidInvestmentTransactionId { get; set; } = "";
    public int AccountId { get; set; }
    public Account? Account { get; set; }
    public int? SecurityId { get; set; }
    public Security? Security { get; set; }
    public DateTime Date { get; set; }
    public string Name { get; set; } = "";
    public string Type { get; set; } = "";               // buy, sell, cash, fee, transfer, cancel
    public string Subtype { get; set; } = "";            // contribution, dividend, buy, sell, management fee, …
    public decimal Quantity { get; set; }
    public decimal Price { get; set; }
    public decimal Amount { get; set; }                  // Plaid's sign: positive = cash debited (e.g. a purchase)
    public decimal Fees { get; set; }
}
