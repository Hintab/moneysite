using Microsoft.AspNetCore.DataProtection;
using MoneySite.Data;
using MoneySite.Services;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

var dbPath = Environment.GetEnvironmentVariable("MONEYSITE_DB_PATH")
            ?? Path.Combine(builder.Environment.ContentRootPath, "data", "money.db");
Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);

// Blazor Server keeps a component alive for the whole browser session, so each
// operation gets its own short-lived DbContext from the factory instead of sharing one.
builder.Services.AddDbContextFactory<MoneyContext>(o => o.UseSqlite($"Data Source={dbPath}"));
// Keys that protect Blazor's connection and form tokens live beside the database, so a
// restart (or container update) doesn't break pages that are already open.
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(Path.GetDirectoryName(dbPath)!, "keys")));
builder.Services.AddSingleton(sp => new BackupService(
    sp.GetRequiredService<IDbContextFactory<MoneyContext>>(), sp.GetRequiredService<Categorizer>(), dbPath));
builder.Services.AddSingleton<DataWindow>();
builder.Services.AddSingleton<Categorizer>();
builder.Services.AddSingleton<ReportService>();
builder.Services.AddSingleton<MerchantService>();
builder.Services.AddHttpClient<PlaidService>(c => c.Timeout = TimeSpan.FromSeconds(60));
builder.Services.AddHostedService<PlaidSyncWorker>();
builder.Services.AddRazorComponents().AddInteractiveServerComponents();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    using var db = scope.ServiceProvider.GetRequiredService<IDbContextFactory<MoneyContext>>().CreateDbContext();
    db.Database.EnsureCreated();
    DbSeed.EnsureSchema(db);
    DbSeed.EnsureSeed(db);
    DbSeed.BackfillMerchants(db);
}
// Re-run automatic categories so rule/logic changes reach existing transactions
// (categories picked by hand are never changed).
var categorizer = app.Services.GetRequiredService<Categorizer>();
await categorizer.MoveCompanySettingsToKeywordsAsync();
await categorizer.CategorizeAsync(reapplyAll: true);

if (!app.Environment.IsDevelopment())
    app.UseExceptionHandler("/error", createScopeForErrors: true);

app.UseStaticFiles();
app.UseAntiforgery();
// Whole-database backup for moving to another machine (the Accounts page links here).
app.MapGet("/backup/download", (BackupService backups) =>
{
    var file = backups.CreateBackupFile();
    var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None, 81920, FileOptions.DeleteOnClose);
    return Results.File(stream, "application/octet-stream", $"moneysite-backup-{DateTime.Now:yyyy-MM-dd}.db");
});
app.MapRazorComponents<MoneySite.Components.App>().AddInteractiveServerRenderMode();
app.Run();
