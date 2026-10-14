namespace Dexter;

/// <summary>
/// The service loop: wait for mail (IMAP IDLE push, re-checked every two minutes as a safety
/// net), hand each new request to the dispatcher, repeat. Agent runs happen off this loop.
/// </summary>
public sealed class RelayWorker(
    SessionStore store,
    IMailbox mailbox,
    Dispatcher dispatcher,
    StatusReporter reporter,
    ILogger<RelayWorker> log) : BackgroundService
{
    private const string BaselineKey = "baseline-done";

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        dispatcher.Start(ct);
        var startupDone = false;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (!startupDone)
                {
                    await StartupAsync(ct);
                    startupDone = true;
                }

                foreach (var mail in await mailbox.FetchNewAsync(store.IsProcessed, ct))
                {
                    store.MarkProcessed(mail.MessageId);
                    await dispatcher.HandleAsync(mail, ct);
                }

                await mailbox.WaitForChangeAsync(TimeSpan.FromMinutes(2), ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                log.LogError("Mail loop error: {Message}. Retrying in 30s.", ex.Message);
                await Task.Delay(TimeSpan.FromSeconds(30), ct).ContinueWith(_ => { });
            }
        }
    }

    private async Task StartupAsync(CancellationToken ct)
    {
        // On the very first run, treat everything already in the mailbox as handled.
        if (store.Get(BaselineKey) is null)
        {
            var existing = await mailbox.FetchNewAsync(_ => false, ct);
            foreach (var mail in existing) store.MarkProcessed(mail.MessageId);
            store.Set(BaselineKey, DateTimeOffset.Now.ToString("O"));
            log.LogInformation("First start: skipped {Count} existing request(s)", existing.Count);
        }

        // Also tells the phone assistant which address Dexter uses.
        await reporter.SendAsync(ct);
        log.LogInformation("Dexter is listening");
    }
}
