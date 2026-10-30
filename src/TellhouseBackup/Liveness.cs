using System.Globalization;
using System.Text.Json;

namespace TellhouseBackup;

// Reset protection, the tool side. While the node runs it holds
// a liveness lock beside its database (see NodeLivenessService in
// HomeNode): a small JSON file carrying the instance id, process,
// and a heartbeat the node refreshes every 10 seconds. Every
// operation in this tool that replaces the database file (restore,
// demo-reset, at-rest seal and unseal) asks here first:
//
//   - no lock file: no node is holding the database; proceed.
//   - a lock whose heartbeat is older than StaleAfter: the node is
//     treated as stopped (a hard kill leaves the lock behind), the
//     stale lock is removed, and the operation proceeds.
//   - a lock that cannot be read at all: never blocks a reset; a
//     garbled lock proves nothing about a live node.
//   - a fresh lock: the operation is refused, unless the operator
//     passes --override-live-node, in which case the reset goes
//     ahead and the override is appended to the reset audit log
//     beside the database so the forcing is itself on record.
//
// The guard can only see a lock that sits beside the database file
// it is given, so it enforces exactly where the reset runs against
// the node's actual database directory (the same directory the node
// writes its lock into): one container, one host, or containers
// sharing that directory through a volume. A reset performed by
// copying files around this tool, or against a copied database, is
// outside what any file guard can see; the stop-the-node-first rule
// in DEPLOY.md remains the protection there. DEPLOY.md states the
// per-shape story in operator terms.
public static class NodeLiveness
{
    public const string LockSuffix = ".liveness";
    public const string AuditLogSuffix = ".reset-audit.log";

    // Six missed heartbeats at the node's 10 second interval. Long
    // enough that a loaded node never looks dead, short enough that
    // a crashed node stops blocking resets within about a minute.
    public static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(60);

    public sealed record LockInfo(string InstanceId, long ProcessId, string MachineName,
        DateTime StartedAtUtc, DateTime HeartbeatAtUtc);

    public static string LockPathFor(string dbPath) => dbPath + LockSuffix;
    public static string AuditLogPathFor(string dbPath) => dbPath + AuditLogSuffix;

    // Null when no lock file exists or it cannot be parsed; callers
    // distinguish "no file" from "unreadable" via File.Exists.
    public static LockInfo? Read(string dbPath)
    {
        var path = LockPathFor(dbPath);
        try
        {
            if (!File.Exists(path)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            string Str(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString() ?? "" : "";
            long Num(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
                && v.TryGetInt64(out var n) ? n : 0;
            DateTime When(string name) => DateTime.TryParse(Str(name), CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var t)
                ? t : DateTime.MinValue;
            var heartbeat = When("heartbeatAtUtc");
            if (heartbeat == DateTime.MinValue) return null;
            return new LockInfo(Str("instanceId"), Num("processId"), Str("machineName"),
                When("startedAtUtc"), heartbeat);
        }
        catch
        {
            return null;
        }
    }

    // Throws InvalidOperationException when a live node holds the
    // database and the caller did not pass the override. With the
    // override, appends the audit line before returning so a forced
    // reset is on record even if the reset itself then fails.
    public static void AssertResetAllowed(string dbPath, bool overrideLiveNode, string operation)
    {
        var lockPath = LockPathFor(dbPath);
        if (!File.Exists(lockPath)) return;
        var info = Read(dbPath);
        if (info is null)
        {
            Console.WriteLine($"note: a node liveness file sits beside {dbPath} but cannot be read; "
                + "treating it as no live node (an unreadable lock never blocks a reset).");
            return;
        }
        var age = DateTime.UtcNow - info.HeartbeatAtUtc;
        if (age < TimeSpan.Zero) age = TimeSpan.Zero; // clock skew reads as fresh, never as stale
        if (age > StaleAfter)
        {
            Console.WriteLine($"note: the node liveness lock beside {dbPath} is stale "
                + $"(last heartbeat {(int)age.TotalSeconds}s ago, instance {info.InstanceId}); "
                + "the node is treated as stopped and the stale lock is removed.");
            try { File.Delete(lockPath); } catch { /* best effort; the node overwrites it on start */ }
            return;
        }
        if (!overrideLiveNode)
            throw new InvalidOperationException(
                $"refusing to {operation}: a live node holds this database (instance {info.InstanceId}, "
                + $"process {info.ProcessId} on {info.MachineName}, started {info.StartedAtUtc:O}, "
                + $"heartbeat {(int)age.TotalSeconds}s ago). Stop the node first and re-run, or re-run "
                + "with --override-live-node to force it and log the override.");
        var line = $"{DateTime.UtcNow:O} override-live-node operation={operation} database={dbPath} "
            + $"lock-instance={info.InstanceId} lock-process={info.ProcessId} lock-machine={info.MachineName} "
            + $"lock-started={info.StartedAtUtc:O} lock-heartbeat={info.HeartbeatAtUtc:O}";
        File.AppendAllText(AuditLogPathFor(dbPath), line + "\n");
        Console.WriteLine($"warning: --override-live-node: forcing {operation} while a live node holds "
            + $"{dbPath}; the override is logged in {AuditLogPathFor(dbPath)}.");
    }
}
