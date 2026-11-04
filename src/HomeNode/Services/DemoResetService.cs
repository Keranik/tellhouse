using Microsoft.Data.Sqlite;

namespace HomeNode.Services;

// The demo's reset between visitors. The demo is open and shared: one
// household, many visitors, one after another. After a quiet stretch
// the house puts its content back to a baseline, so the next visitor
// starts from the same day instead of the last visitor's messages.
//
// What it restores, and what it never touches, stated plainly:
// - Restored from the baseline: the household's content (every
//   message, entry, claim, plan, supply count, delivery, reading and
//   model call). Anything a visitor added is removed.
// - Never touched: the household and its people, and everything that
//   keeps the members' own assistants connected (connections, invites,
//   join requests, consents) and the house's replay protection
//   (session challenges, seen packets, used nonces). A reset therefore
//   leaves Lisa's and Daniel's assistants connected, and a packet the
//   house has already seen can never land a second time.
//
// The baseline is a copy of the database taken with SQLite's own
// VACUUM INTO, so it is a consistent snapshot even while the node
// runs. The restore runs inside one transaction on the live database
// through SQLite itself (never a file swapped under the running
// node), so other requests see the old content or the new, never a
// mix. In local demo mode the baseline is taken at startup; on a fresh
// deployment the operator takes it once the assistants are connected
// (POST /admin/baseline, DEPLOY.md step 9).
public sealed class DemoResetService
{
    // Tables a reset leaves exactly as they are.
    public static readonly IReadOnlySet<string> KeptTables = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "Households", "Persons",
        "HouseConnections", "DoorInvites", "JoinRequests", "ConsentRecords",
        "SessionChallenges", "PacketSeen", "PacketNonceUses"
    };

    private readonly string _connectionString;
    private readonly string _databasePath;
    private readonly ILogger<DemoResetService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _clock = new();
    private DateTime _lastActivityUtc = DateTime.UtcNow;
    private bool _changedSinceBaseline;
    private DateTime _lastStartOverUtc = DateTime.MinValue;

    public DemoResetService(IConfiguration configuration, ILogger<DemoResetService> logger)
    {
        _connectionString = configuration.GetConnectionString("HomeNode") ?? "Data Source=home-node.db";
        var builder = new SqliteConnectionStringBuilder(_connectionString);
        _databasePath = Path.GetFullPath(string.IsNullOrWhiteSpace(builder.DataSource) ? "home-node.db" : builder.DataSource);
        _logger = logger;
        IdleMinutes = configuration.GetValue("Demo:IdleResetMinutes", 20);
        StartOverAllowed = configuration.GetValue("Demo:StartOver", true);
    }

    // Minutes without a visitor's change before the house resets
    // itself; 0 turns the automatic reset off.
    public int IdleMinutes { get; }
    public bool StartOverAllowed { get; }
    public string BaselinePath => _databasePath + ".baseline";
    public bool HasBaseline => File.Exists(BaselinePath);
    public DateTime? LastResetUtc { get; private set; }

    // Any visitor's change (a page form post) counts as activity and
    // marks the house as changed since its baseline. Reads, pings and
    // health checks do not.
    public void NoteActivity()
    {
        lock (_clock)
        {
            _lastActivityUtc = DateTime.UtcNow;
            _changedSinceBaseline = true;
        }
    }

    public bool DueForQuietReset(DateTime nowUtc)
    {
        if (IdleMinutes <= 0 || !HasBaseline)
        {
            return false;
        }
        lock (_clock)
        {
            return _changedSinceBaseline && nowUtc - _lastActivityUtc >= TimeSpan.FromMinutes(IdleMinutes);
        }
    }

    public async Task CaptureBaselineAsync(CancellationToken cancel = default)
    {
        await _gate.WaitAsync(cancel);
        try
        {
            var temp = BaselinePath + ".tmp";
            File.Delete(temp);
            await using (var connection = new SqliteConnection(_connectionString))
            {
                await connection.OpenAsync(cancel);
                await using var command = connection.CreateCommand();
                command.CommandText = "VACUUM INTO $path";
                command.Parameters.AddWithValue("$path", temp);
                await command.ExecuteNonQueryAsync(cancel);
            }
            File.Move(temp, BaselinePath, overwrite: true);
            lock (_clock)
            {
                _changedSinceBaseline = false;
            }
            _logger.LogInformation("Demo reset: baseline taken at {Path}", BaselinePath);
        }
        finally
        {
            _gate.Release();
        }
    }

    // Puts the household's content back to the baseline. Returns false
    // (and changes nothing) when there is no baseline yet.
    public async Task<bool> ResetAsync(string reason, CancellationToken cancel = default)
    {
        if (!HasBaseline)
        {
            return false;
        }
        await _gate.WaitAsync(cancel);
        try
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancel);
            await Exec(connection, "PRAGMA foreign_keys = OFF", cancel);
            await using (var attach = connection.CreateCommand())
            {
                attach.CommandText = "ATTACH DATABASE $path AS baseline";
                attach.Parameters.AddWithValue("$path", BaselinePath);
                await attach.ExecuteNonQueryAsync(cancel);
            }
            try
            {
                var live = await TablesAsync(connection, "main", cancel);
                var saved = await TablesAsync(connection, "baseline", cancel);
                await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancel);
                var restored = 0;
                foreach (var table in live.Where(t => !KeptTables.Contains(t) && saved.Contains(t)))
                {
                    await Exec(connection, $"DELETE FROM main.\"{table}\"", cancel, transaction);
                    await Exec(connection, $"INSERT INTO main.\"{table}\" SELECT * FROM baseline.\"{table}\"", cancel, transaction);
                    restored++;
                }
                await transaction.CommitAsync(cancel);
                _logger.LogInformation("Demo reset ({Reason}): {Count} content tables put back to the baseline; connections untouched", reason, restored);
            }
            finally
            {
                await Exec(connection, "DETACH DATABASE baseline", CancellationToken.None);
                // The connection goes back to the pool as it came.
                await Exec(connection, "PRAGMA foreign_keys = ON", CancellationToken.None);
            }
            lock (_clock)
            {
                _changedSinceBaseline = false;
                LastResetUtc = DateTime.UtcNow;
            }
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    // The visitor's own "Start over": at most once every two minutes,
    // so one visitor cannot keep resetting the house under another.
    public async Task<bool> StartOverAsync()
    {
        if (!StartOverAllowed || !HasBaseline)
        {
            return false;
        }
        lock (_clock)
        {
            if (DateTime.UtcNow - _lastStartOverUtc < TimeSpan.FromMinutes(2))
            {
                return false;
            }
            _lastStartOverUtc = DateTime.UtcNow;
        }
        return await ResetAsync("start over");
    }

    private static async Task<HashSet<string>> TablesAsync(SqliteConnection connection, string schema, CancellationToken cancel)
    {
        var tables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT name FROM {schema}.sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%'";
        await using var reader = await command.ExecuteReaderAsync(cancel);
        while (await reader.ReadAsync(cancel))
        {
            tables.Add(reader.GetString(0));
        }
        return tables;
    }

    private static async Task Exec(SqliteConnection connection, string sql, CancellationToken cancel, SqliteTransaction? transaction = null)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction;
        await command.ExecuteNonQueryAsync(cancel);
    }
}

// The quiet-stretch timer: checks twice a minute and resets the house
// once nobody has changed anything for the configured minutes. In local
// demo mode it also takes the baseline at startup.
public sealed class DemoResetTimer : BackgroundService
{
    private readonly DemoResetService _reset;
    private readonly ILogger<DemoResetTimer> _logger;
    private readonly bool _captureAtStart;

    public DemoResetTimer(DemoResetService reset, ILogger<DemoResetTimer> logger, IConfiguration configuration)
    {
        _reset = reset;
        _logger = logger;
        var mode = Environment.GetEnvironmentVariable("TELLHOUSE_DEPLOY_MODE") ?? "demo";
        _captureAtStart = configuration.GetValue("Demo:BaselineAtStartup",
            !mode.Equals("fresh", StringComparison.OrdinalIgnoreCase));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            if (_captureAtStart && !_reset.HasBaseline)
            {
                await _reset.CaptureBaselineAsync(stoppingToken);
            }
        }
        catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Demo reset: the startup baseline could not be taken");
        }
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
                if (_reset.DueForQuietReset(DateTime.UtcNow))
                {
                    await _reset.ResetAsync("quiet for " + _reset.IdleMinutes + " minutes", stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Demo reset: the quiet reset did not run");
            }
        }
    }
}
