using Microsoft.EntityFrameworkCore;
using HomeNode.Data;

namespace HomeNode.Services;

// The node's relay loop. The home node built the
// relay client, sealed intake, and the sealed outbox as services;
// nothing ran them against a live relay. This loop
// is that wiring, and only that wiring: poll the relay as the
// node-role connection, hand every wire packet to sealed intake,
// process the queued inbox through the same pipeline, then post
// every queued outbox packet to the relay and mark it Sent (a
// member's acknowledgement later marks it Acknowledged, in
// intake). It runs only when Relay:BaseUrl is configured. A
// telling is recorded only when intake takes it in, never
// because this loop moved it.
public class RelayLoopService : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly IConfiguration _config;
    private readonly ILogger<RelayLoopService> _logger;

    public RelayLoopService(IServiceScopeFactory scopes, IConfiguration config, ILogger<RelayLoopService> logger)
    {
        _scopes = scopes;
        _config = config;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var baseUrl = _config.GetValue<string>("Relay:BaseUrl");
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return;
        }
        _logger.LogInformation("Relay loop started for {BaseUrl}", baseUrl);
        var nodeConnectionId = _config.GetValue<string>("Relay:NodeConnectionId") ?? "node-conn-01";
        var nodeToken = Environment.GetEnvironmentVariable("RELAY_NODE_TOKEN")
            ?? _config.GetValue<string>("Relay:NodeToken") ?? "";
        // The stand-ins' measured finding, adopted here: on this network
        // dual-mode (IPv4-mapped IPv6) sockets are reset by the
        // interception layer while plain IPv4 sockets pass, and
        // mesh traffic must never ride a proxy. The relay client
        // therefore gets a plain IPv4, proxy-free HttpClient.
        using var http = new HttpClient(new SocketsHttpHandler
        {
            UseProxy = false,
            ConnectCallback = async (ctx, ct) =>
            {
                var sock = new System.Net.Sockets.Socket(
                    System.Net.Sockets.AddressFamily.InterNetwork,
                    System.Net.Sockets.SocketType.Stream,
                    System.Net.Sockets.ProtocolType.Tcp);
                await sock.ConnectAsync(ctx.DnsEndPoint.Host, ctx.DnsEndPoint.Port, ct);
                return new System.Net.Sockets.NetworkStream(sock, ownsSocket: true);
            }
        })
        { Timeout = TimeSpan.FromSeconds(10) };
        var client = new RelayClientService(http, baseUrl, nodeConnectionId, nodeToken);
        var endpoint = SealedDeliveryService.HouseholdEndpointFor(1);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopes.CreateScope();
                var intake = scope.ServiceProvider.GetRequiredService<SealedIntakeService>();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                await RunPassAsync(client, intake, db, endpoint, _logger, stoppingToken);
            }
            catch (Exception ex)
            {
                if (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                // Any pass failure (relay down, timeout, a bad
                // packet) is retried on the next pass; the loop
                // must never take the host down with it.
                _logger.LogDebug(ex, "Relay loop pass failed, retrying");
            }
            try
            {
                await Task.Delay(500, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    // One poll pass, extracted so the node suite can drive the
    // real poll path end to end. Per-packet isolation throughout:
    // the relay drops every packet it forwards, so
    // nothing this pass has fetched may be lost because a sibling
    // packet was bad. A packet intake refuses is a rejection
    // receipt with a trail line; a packet whose handling throws
    // is logged and the pass continues with the next one; queued
    // processing isolates its own failures inside
    // ProcessAllQueuedAsync; an outbox item that fails to post
    // stays Queued for the next pass, never marked Sent.
    public static async Task RunPassAsync(
        RelayClientService client, SealedIntakeService intake, AppDbContext db,
        string householdEndpoint, ILogger? logger = null, CancellationToken ct = default)
    {
        var packets = await client.PollHouseholdAsync(householdEndpoint, ct);
        if (client.LastPollDroppedMalformed > 0)
        {
            logger?.LogWarning(
                "Relay poll dropped {Count} malformed packet(s) that could not be rebuilt; the valid packets in the batch continue to intake",
                client.LastPollDroppedMalformed);
        }
        foreach (var packet in packets)
        {
            try
            {
                await intake.ReceiveWireAsync(packet.WireJson);
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex,
                    "Relay loop: one fetched packet failed intake and was skipped; the rest of the batch continues");
            }
        }
        await intake.ProcessAllQueuedAsync();
        // Deliveries are owed until acknowledged. The
        // relay drops a store-and-forward packet 300 seconds after
        // it is posted if nobody collects it, so a Sent item that
        // is still unacknowledged past that window is queued again
        // here and posted again below. Acknowledgements are
        // processed earlier in this same pass, so an item whose
        // acknowledgement just arrived is never re-posted.
        // Session challenges are excluded: their proof exchange
        // has its own 120 second lifetime and a stale challenge
        // must never be revived.
        var staleCutoff = DateTime.UtcNow.AddSeconds(-310);
        var staleSent = await db.SealedOutbox
            .Where(o => o.Status == "Sent" && o.AcknowledgedAtUtc == null
                && o.SourceKind != "SessionChallenge"
                && ((o.SentAtUtc != null && o.SentAtUtc <= staleCutoff)
                    || (o.SentAtUtc == null && o.CreatedAtUtc <= staleCutoff)))
            .ToListAsync(ct);
        foreach (var item in staleSent)
        {
            item.Status = "Queued";
            logger?.LogWarning(
                "Relay loop: outbox packet {PacketId} was sent and never acknowledged past the relay window; queueing it again",
                item.PacketId);
        }
        if (staleSent.Count > 0)
        {
            await db.SaveChangesAsync(ct);
        }
        var queued = await db.SealedOutbox
            .Where(o => o.Status == "Queued")
            .OrderBy(o => o.CreatedAtUtc)
            .ToListAsync(ct);
        foreach (var item in queued)
        {
            try
            {
                await client.PostPacketAsync(item.WireJson, ct);
                item.Status = "Sent";
                item.SentAtUtc = DateTime.UtcNow;
                await db.SaveChangesAsync(ct);
            }
            catch (Exception ex)
            {
                if (ct.IsCancellationRequested)
                {
                    throw;
                }
                logger?.LogWarning(ex,
                    "Relay loop: one outbox packet failed to post and stays queued for the next pass");
            }
        }
    }
}
