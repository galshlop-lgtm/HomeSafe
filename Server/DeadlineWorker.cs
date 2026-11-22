using HomeSafe.Shared;

namespace HomeSafe.Server;

public sealed class DeadlineWorker(Store store, PushSender sender, TimeProvider clock, ILogger<DeadlineWorker> log)
    : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.WhenAll(
        RunRepeatedly(AdvanceDeadlines, stoppingToken), RunRepeatedly(Dispatch, stoppingToken));

    private async Task RunRepeatedly(Func<CancellationToken, Task> action, CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2), clock);
        while (!token.IsCancellationRequested)
        {
            try { await action(token); }
            catch (Exception ex) when (!token.IsCancellationRequested)
            {
                log.LogError(ex, "HomeSafe worker will retry after an error");
            }
            if (!await timer.WaitForNextTickAsync(token)) return;
        }
    }

    private Task AdvanceDeadlines(CancellationToken token)
    {
        var now = clock.GetUtcNow();
        store.Write(s => ReturnWork.Advance(s, now), changed => changed);
        return Task.CompletedTask;
    }

    private async Task Dispatch(CancellationToken token)
    {
        var now = clock.GetUtcNow();
        var pending = store.Read(s => s.Outbox.Where(d => !d.Accepted && d.ReceivedAt is null && d.NextTry <= now && d.Message.ExpiresAt > now)
            .Select(d => (d.Message, d.Recipient))
            .OrderByDescending(x => x.Message.Urgent)
            .Take(100).ToArray());
        await Parallel.ForEachAsync(pending, new ParallelOptions
        {
            MaxDegreeOfParallelism = 12, CancellationToken = token
        }, async (item, ct) =>
        {
            var target = store.Read(s =>
            {
                var delivery = s.Outbox.Find(d => d.Message.Id == item.Message.Id);
                return delivery is null ? null : s.Users.Find(u => u.Account.Id == item.Recipient)?.Device;
            });
            var result = target is null ? DeliveryResult.Retry : await sender.Send(target, item.Message, ct);
            store.Write(s =>
            {
                var delivery = s.Outbox.Find(d => d.Message.Id == item.Message.Id);
                if (delivery is null) return false;
                delivery.Attempts++;
                delivery.Accepted = result == DeliveryResult.Accepted;
                delivery.NextTry = clock.GetUtcNow().AddSeconds(Math.Min(90, 5 * Math.Pow(2, delivery.Attempts - 1)));
                var recipient = s.Users.Find(u => u.Account.Id == item.Recipient);
                if (result == DeliveryResult.InvalidDevice && recipient?.Device == target)
                    recipient!.Device = null;
                return true;
            }, changed => changed);
        });
    }
}
