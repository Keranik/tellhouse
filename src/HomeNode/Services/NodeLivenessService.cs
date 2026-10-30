using System.Text.Json;

namespace HomeNode.Services;

// Reset protection, the node side. While the node runs, this
// service holds a liveness lock beside the database file (the
// database path plus ".liveness") and refreshes its heartbeat every
// HeartbeatInterval. The backup and reset tool (TellhouseBackup)
// reads the same file before it replaces a database: a fresh
// heartbeat refuses the reset (or forces the operator through a
// logged override), a heartbeat older than the tool's staleness
// bound (60 seconds, six missed beats) reads as a stopped node.
//
// The lock is deleted on a graceful stop. A hard kill leaves it
// behind, and the staleness rule is what lets resets proceed again;
// that trade is deliberate, so a crashed node never wedges the
// reset path shut.
//
// The lock protects exactly the resets that run against this
// database's own directory through the tool: one container, one
// host, or containers sharing that directory. It cannot see a
// reset performed by copying files around the tool, and DEPLOY.md
// says so in operator terms.
public sealed class NodeLivenessService : BackgroundService
{
    public static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(10);

    private readonly IConfiguration _config;
    private readonly ILogger<NodeLivenessService> _log;
    private readonly string _instanceId = Guid.NewGuid().ToString("N");
    private readonly DateTime _startedAtUtc = DateTime.UtcNow;

    public NodeLivenessService(IConfiguration config, ILogger<NodeLivenessService> log)
    {
        _config = config;
        _log = log;
    }

    public static string LockPathFor(string dbPath) => dbPath + ".liveness";

    // The file database behind the HomeNode connection string, or
    // null when the connection names no file (the lock only makes
    // sense beside a real database file).
    public static string? DatabasePath(IConfiguration config)
    {
        var cs = config.GetConnectionString("HomeNode");
        if (string.IsNullOrWhiteSpace(cs)) return null;
        foreach (var part in cs.Split(';'))
        {
            var kv = part.Split('=', 2);
            if (kv.Length != 2
                || !kv[0].Trim().Equals("Data Source", StringComparison.OrdinalIgnoreCase))
                continue;
            var value = kv[1].Trim();
            if (value.Length == 0 || value == ":memory:") return null;
            return Path.GetFullPath(value);
        }
        return null;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var dbPath = DatabasePath(_config);
        if (dbPath is null)
        {
            _log.LogWarning("Node liveness lock not held: the HomeNode connection string names no database file.");
            return;
        }
        var lockPath = LockPathFor(dbPath);
        try
        {
            Write(lockPath);
            _log.LogInformation("Node liveness lock held at {LockPath} (instance {Instance}).",
                lockPath, _instanceId);
            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(HeartbeatInterval, stoppingToken);
                Write(lockPath);
            }
        }
        catch (OperationCanceledException)
        {
            // Graceful stop; the finally below releases the lock.
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Node liveness heartbeat failed; resets against this database "
                + "fall back to the tool's staleness rule and the stop-first operator rule.");
        }
        finally
        {
            Release(lockPath);
        }
    }

    private void Write(string lockPath)
    {
        var payload = JsonSerializer.Serialize(new
        {
            format = "tellhouse-node-liveness",
            version = 1,
            instanceId = _instanceId,
            processId = Environment.ProcessId,
            machineName = Environment.MachineName,
            startedAtUtc = _startedAtUtc.ToString("O"),
            heartbeatAtUtc = DateTime.UtcNow.ToString("O"),
        });
        var tmp = lockPath + ".tmp-" + _instanceId;
        File.WriteAllText(tmp, payload);
        File.Move(tmp, lockPath, overwrite: true);
    }

    private void Release(string lockPath)
    {
        try
        {
            // Delete only our own lock: a successor instance may
            // already hold the path.
            if (File.Exists(lockPath)
                && File.ReadAllText(lockPath).Contains(_instanceId, StringComparison.Ordinal))
                File.Delete(lockPath);
        }
        catch
        {
            // Best effort. A leftover lock goes stale on the tool's
            // documented rule and never wedges a reset shut.
        }
    }
}
