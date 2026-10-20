using Microsoft.EntityFrameworkCore;
using MoneySite.Data;

namespace MoneySite.Services;

/// Syncs each connected bank once every 24 hours, so transactions stay current and a daily
/// balance snapshot builds up net-worth history. It checks hourly which banks are due, so
/// restarting the app doesn't cause extra syncs. "Sync now" on the Accounts page still
/// works any time.
public class PlaidSyncWorker : BackgroundService
{
    public static readonly TimeSpan SyncEvery = TimeSpan.FromHours(24);
    private static readonly TimeSpan CheckEvery = TimeSpan.FromHours(1);
    private static readonly TimeSpan RetryFailedAfter = TimeSpan.FromHours(6);

    private readonly IServiceProvider _services;
    private readonly ILogger<PlaidSyncWorker> _log;
    private readonly Dictionary<int, DateTime> _lastAttempt = new();

    public PlaidSyncWorker(IServiceProvider services, ILogger<PlaidSyncWorker> log)
    {
        _services = services;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);
        using var timer = new PeriodicTimer(CheckEvery);
        do
        {
            await SyncDueAsync(stoppingToken);
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task SyncDueAsync(CancellationToken ct)
    {
        using var scope = _services.CreateScope();
        var plaid = scope.ServiceProvider.GetRequiredService<PlaidService>();
        if (!plaid.IsConfigured) return;

        var dbf = scope.ServiceProvider.GetRequiredService<IDbContextFactory<MoneyContext>>();
        await using var db = await dbf.CreateDbContextAsync(ct);
        var now = DateTime.UtcNow;
        var items = await db.PlaidItems.Select(p => new { p.Id, p.LastSyncedAt }).ToListAsync(ct);
        foreach (var item in items)
        {
            var due = item.LastSyncedAt is null || now - item.LastSyncedAt.Value >= SyncEvery;
            // A failed sync doesn't update LastSyncedAt; don't retry it every hour.
            var triedRecently = _lastAttempt.TryGetValue(item.Id, out var tried) && now - tried < RetryFailedAfter;
            if (!due || triedRecently) continue;

            _lastAttempt[item.Id] = now;
            try { await plaid.PullItemAsync(item.Id, ct); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { _log.LogWarning(ex, "Scheduled Plaid sync failed for item {Id}", item.Id); }
        }
    }
}
