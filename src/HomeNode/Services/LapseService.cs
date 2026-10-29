namespace HomeNode.Services;

// The timer behind the confirmation flow's one timer-driven move:
// an unanswered proposal lapses to NotConfirmed after 24 hours
// of silence, with the system as actor and
// "lapse" as the audit channel. Silence is never a yes; the lapse
// is the only state change in the system that no person performs,
// and it only ever discards a proposal, never a record-pending
// fact and never anything a person has already decided.
//
// The sweep runs once at startup and then on an interval. The
// 24-hour deadline itself is the design's; the sweep interval is an
// implementation detail (a proposal lapses at most one interval
// after its deadline, never before it).
public class LapseService : BackgroundService
{
    private static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(15);

    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<LapseService> _logger;

    public LapseService(IServiceScopeFactory scopes, ILogger<LapseService> logger)
    {
        _scopes = scopes;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(SweepInterval);
        do
        {
            try
            {
                using var scope = _scopes.CreateScope();
                var confirmations = scope.ServiceProvider.GetRequiredService<ConfirmationService>();
                var lapsed = await confirmations.LapseExpiredAsync(DateTime.UtcNow, stoppingToken);
                if (lapsed > 0)
                {
                    _logger.LogInformation("Lapsed {Count} unanswered proposal(s) past the 24-hour mark.", lapsed);
                }
                // The audience ask lapse (spec C.2.1 clause 7): a
                // presented ask unanswered past its clock lapses, and
                // the event stays at its narrowest audience. Silence
                // never widens, so this sweep changes no audience.
                var audience = scope.ServiceProvider.GetRequiredService<AudienceDecisionService>();
                var asksLapsed = await audience.LapseAsksAsync(DateTime.UtcNow, stoppingToken);
                if (asksLapsed > 0)
                {
                    _logger.LogInformation("Lapsed {Count} unanswered audience ask(s); their events stay at their narrowest audience.", asksLapsed);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // A failed sweep never takes the app down; the next
                // interval sweeps again, and proposals stay visibly
                // awaiting an answer in the meantime.
                _logger.LogWarning("Lapse sweep failed; will retry next interval: {Message}", ex.Message);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
