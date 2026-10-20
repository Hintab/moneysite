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
app.MapRazorComponents<MoneySite.Components.App>().AddInteractiveServerRenderMode();
app.Run();
