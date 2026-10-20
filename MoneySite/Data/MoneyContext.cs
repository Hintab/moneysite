using Microsoft.EntityFrameworkCore;

namespace MoneySite.Data;

public class MoneyContext : DbContext
{
    public MoneyContext(DbContextOptions<MoneyContext> options) : base(options) { }

    public DbSet<Account> Accounts => Set<Account>();
    public DbSet<Transaction> Transactions => Set<Transaction>();
    public DbSet<Category> Categories => Set<Category>();
    public DbSet<CategorizationRule> CategorizationRules => Set<CategorizationRule>();
    public DbSet<BalanceSnapshot> BalanceSnapshots => Set<BalanceSnapshot>();
    public DbSet<PlaidItem> PlaidItems => Set<PlaidItem>();
    public DbSet<Merchant> Merchants => Set<Merchant>();
    public DbSet<MerchantAlias> MerchantAliases => Set<MerchantAlias>();
    public DbSet<AppSetting> AppSettings => Set<AppSetting>();
    public DbSet<Security> Securities => Set<Security>();
    public DbSet<Holding> Holdings => Set<Holding>();
    public DbSet<InvestmentTransaction> InvestmentTransactions => Set<InvestmentTransaction>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        // Not unique: the same bank can be linked twice, and two identical purchases
        // on the same day are legitimate. Plaid transactions are de-duplicated by PlaidTransactionId.
        b.Entity<Account>().HasIndex(a => a.Name);
        b.Entity<Category>().HasIndex(c => c.Name).IsUnique();
        b.Entity<Transaction>().HasIndex(t => new { t.AccountId, t.Date, t.Description, t.Amount });
        b.Entity<Transaction>().HasIndex(t => t.PlaidTransactionId);
        b.Entity<Transaction>().HasIndex(t => t.Date);
        b.Entity<Transaction>().HasIndex(t => t.Merchant);
        b.Entity<Merchant>().HasIndex(m => m.Name).IsUnique();
        b.Entity<Merchant>().HasOne(m => m.Category).WithMany().OnDelete(DeleteBehavior.SetNull);
        b.Entity<MerchantAlias>().HasIndex(a => a.Raw).IsUnique();
        b.Entity<AppSetting>().HasKey(s => s.Key);
        b.Entity<Security>().HasIndex(x => x.PlaidSecurityId).IsUnique();
        b.Entity<InvestmentTransaction>().HasIndex(x => x.PlaidInvestmentTransactionId).IsUnique();
        b.Entity<Holding>().HasOne(h => h.Account).WithMany().OnDelete(DeleteBehavior.Cascade);
        b.Entity<InvestmentTransaction>().HasOne(x => x.Account).WithMany().OnDelete(DeleteBehavior.Cascade);

        b.Entity<Transaction>().HasOne(t => t.Account).WithMany(a => a.Transactions)
            .OnDelete(DeleteBehavior.Cascade);
        b.Entity<Transaction>().HasOne(t => t.Category).WithMany(c => c.Transactions)
            .OnDelete(DeleteBehavior.SetNull);
        b.Entity<CategorizationRule>().HasOne(r => r.Category).WithMany()
            .OnDelete(DeleteBehavior.Cascade);
        b.Entity<BalanceSnapshot>().HasOne(s => s.Account).WithMany()
            .OnDelete(DeleteBehavior.Cascade);
        b.Entity<Account>().HasOne<PlaidItem>().WithMany(p => p.Accounts)
            .HasForeignKey(a => a.PlaidItemId).OnDelete(DeleteBehavior.SetNull);
    }
}
