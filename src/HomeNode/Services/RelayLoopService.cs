using Microsoft.EntityFrameworkCore;
using HomeNode.Data;

namespace HomeNode.Services;

// The node's relay loop. The home node built the relay client,
// sealed intake, and the sealed outbox as services; this loop
// runs them against a live relay. This loop
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
        // the stand-ins' measured finding, adopted here: on this network
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
                var packets = await client.PollHouseholdAsync(endpoint, stoppingToken);
                using var scope = _scopes.CreateScope();
                var intake = scope.ServiceProvider.GetRequiredService<SealedIntakeService>();
                foreach (var packet in packets)
                {
                    await intake.ReceiveWireAsync(packet.WireJson);
                }
                await intake.ProcessAllQueuedAsync();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var queued = await db.SealedOutbox
                    .Where(o => o.Status == "Queued")
                    .OrderBy(o => o.CreatedAtUtc)
                    .ToListAsync(stoppingToken);
                foreach (var item in queued)
                {
                    await client.PostPacketAsync(item.WireJson, stoppingToken);
                    item.Status = "Sent";
                    await db.SaveChangesAsync(stoppingToken);
                }
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
}
