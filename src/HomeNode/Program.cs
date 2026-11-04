using HomeNode.Data;
using HomeNode.Services;
using HomeNode.Services.Extraction;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

// The local demo suite runs the whole pipeline against a scratch
// slice and exits; it never starts the web host or touches the demo
// database. See Services/DemoSuite.cs. It is the node's regression
// harness: the house layer's 103 checks, assertions unmodified,
// against this project's services.
// The node suite (--node-suite) exercises the home node's own
// work: the door, the valve, sealed intake, sealed delivery, and
// told time versus recorded time. See Services/NodeSuite.cs.
if (args.Contains("--demo-suite"))
{
    return await DemoSuite.RunAsync();
}
if (args.Contains("--node-suite"))
{
    return await NodeSuite.RunAsync();
}

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorPages();
builder.Services.AddScoped<ConfirmationService>();
builder.Services.AddScoped<AudienceDecisionService>();

// The extraction pipeline behind the model-role seam. The mode comes
// from configuration (Extraction:Mode): "Mock" runs the deterministic
// local extractor (development, tests, the demo suite; no credit
// spent), "Live" calls the Meta Model API. Default is Mock.
builder.Services.Configure<ExtractionOptions>(builder.Configuration.GetSection("Extraction"));
// Every path reads through the router: a private message (the Private
// switch, or health-shaped words) goes to the house's own
// deterministic reader and never to a model; see PrivacyRule.
builder.Services.AddScoped<IExtractionClient>(sp =>
{
    var options = sp.GetRequiredService<IOptions<ExtractionOptions>>().Value;
    IExtractionClient model = options.Mode.Equals("Live", StringComparison.OrdinalIgnoreCase)
        ? ActivatorUtilities.CreateInstance<MetaExtractionClient>(sp)
        : new MockExtractionClient();
    return new RoutedExtractionClient(model, new MockExtractionClient());
});
builder.Services.AddScoped<ExtractionContextBuilder>();
builder.Services.AddScoped<ExtractionService>();
// The house's other Meta models (Services/Models): hearing (Muse Voice
// Transcribe), reviewing (Muse Wonder), seeing (SAM 3.1) and drawing
// (Muse Image), all through the Meta Model API with the same key, and
// only on a Live instance. Model ids and paths live under "Models".
builder.Services.Configure<HomeNode.Services.Models.ModelOptions>(builder.Configuration.GetSection("Models"));
builder.Services.AddSingleton<HomeNode.Services.Models.MetaApi>();
builder.Services.AddScoped<HomeNode.Services.Models.IVoiceClient, HomeNode.Services.Models.MetaVoiceClient>();
builder.Services.AddScoped<HomeNode.Services.Models.ReviewService>();
builder.Services.AddScoped<HomeNode.Services.Models.ISeeClient, HomeNode.Services.Models.MetaSeeClient>();
builder.Services.AddScoped<HomeNode.Services.Models.ShelfCheckService>();
builder.Services.AddScoped<HomeNode.Services.Models.IDrawClient, HomeNode.Services.Models.MetaDrawClient>();
// Picture cards for deliveries to children: drawn in the background
// after the delivery is written, and only on a Live instance.
builder.Services.AddHostedService<HomeNode.Services.Models.PictureCardService>();
builder.Services.AddScoped<NeedsYouService>();
builder.Services.AddScoped<PlanAssemblyService>();
builder.Services.AddScoped<RunnerTaskService>();
builder.Services.AddScoped<MemberDeliveryService>();
builder.Services.AddScoped<BriefService>();
builder.Services.AddScoped<LookupService>();

// The Test Send control: a remote trigger to configured
// stand-in assistants, carrying no telling content. Stand-ins are
// configured under Demo:StandinEndpoints; none are configured by
// default. The service is built from configuration and an HttpClient
// only; it holds no database reference and no path into the record.
// The client is plain IPv4 and proxy-free, the same shape the relay
// loop uses (the stand-ins' measured finding, recorded on
// RelayLoopService): on this network dual-mode sockets are reset by
// the interception layer and house traffic must never ride a proxy.
builder.Services.Configure<DemoOptions>(builder.Configuration.GetSection("Demo"));
builder.Services.AddSingleton(new HttpClient(new SocketsHttpHandler
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
// 30 seconds, not 10: a Test Send trigger call now carries the
// stand-in's session recovery inside it. In auto session
// mode the stand-in confirms its session through the real
// exchange before it sends, which takes a few seconds on a
// healthy loop; the timeout must cover that exchange plus the
// send, or the front door would report a timeout on a send that
// is in fact landing.
{ Timeout = TimeSpan.FromSeconds(30) });
builder.Services.AddScoped<TestSendService>(sp =>
{
    var options = sp.GetRequiredService<IOptions<DemoOptions>>().Value;
    return new TestSendService(sp.GetRequiredService<HttpClient>(), options.StandinEndpoints);
});

// The home node's federation services. The node keystore holds the
// node's own content and signing keys in files outside the record
// database (config Node:KeyStorePath, or the HOME_NODE_KEYSTORE
// environment variable, or a node-keys folder beside the app).
builder.Services.AddSingleton<NodeKeyStore>(sp =>
    new NodeKeyStore(
        builder.Configuration.GetValue<string>("Node:KeyStorePath")));
builder.Services.AddScoped<DoorService>();
builder.Services.AddScoped<SealedDeliveryService>();
builder.Services.AddScoped<SealedIntakeService>();

// The stale-issue bound for sealed intake is one named
// configuration value (Intake:MaxPacketAgeDays, shipped default
// 7 days, off supported); PacketAgeBound carries the tuning basis.
builder.Services.AddSingleton(PacketAgeBound.FromConfiguration(builder.Configuration));

// The house side's authorization, one instance shared by the
// HTTP door and admin adapter below and the Connections page:
// one secret, one check, every surface that performs a house
// side act. See Services/HouseAuthorization.cs.
builder.Services.AddSingleton(new HouseAuthorization(
    Environment.GetEnvironmentVariable("HOME_NODE_HOUSE_SECRET")
    ?? builder.Configuration.GetValue<string>("Door:HouseSecret")));

// The relay loop runs only when a relay is configured
// (Relay:BaseUrl). It polls the relay, feeds sealed intake, and
// posts the sealed outbox. See Services/RelayLoopService.cs.
if (!string.IsNullOrWhiteSpace(builder.Configuration.GetValue<string>("Relay:BaseUrl")))
{
    builder.Services.AddHostedService<RelayLoopService>();
}

// The one timer in the system: it runs the 24-hour lapse on
// unanswered proposals (see Services/LapseService.cs).
builder.Services.AddHostedService<LapseService>();

// While the node runs it holds a liveness lock beside its
// database, so the backup and reset tooling can refuse (or demand
// a logged override for) a file swap against a live node, across
// containers wherever the database directory is shared. See
// Services/NodeLivenessService.cs.
builder.Services.AddHostedService<NodeLivenessService>();

// The demo's reset between visitors (Services/DemoResetService.cs):
// after a quiet stretch the household's content goes back to its
// baseline; connections and replay protection are never touched.
builder.Services.AddSingleton<DemoResetService>();
builder.Services.AddHostedService<DemoResetTimer>();

// The shared record lives in one SQLite file beside the app. Delete the
// file to start over; the app creates it and seeds the demo household on
// the next start.
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("HomeNode")));

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.EnsureCreated();
    // Schema compatibility: two nullable columns were added
    // after the baked demo snapshot was sealed (SealedOutbox
    // .SentAtUtc for the alert re-post clock, JoinRequests
    // .ConnectionId for the stored connection id). EnsureCreated
    // never alters an existing file, and a restore swaps in the
    // snapshot's older schema whole, so without this step a
    // restored record throws "no such column" on first use. Add
    // each column when it is absent; both are nullable and every
    // reader treats null as the pre-column meaning.
    var conn = db.Database.GetDbConnection();
    conn.Open();
    void AddColumnIfAbsent(string table, string column, string ddl)
    {
        using var read = conn.CreateCommand();
        read.CommandText = $"PRAGMA table_info({table})";
        var present = false;
        using (var r = read.ExecuteReader())
        {
            while (r.Read())
            {
                if (string.Equals(r.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                {
                    present = true;
                }
            }
        }
        if (!present)
        {
            using var alter = conn.CreateCommand();
            alter.CommandText = ddl;
            alter.ExecuteNonQuery();
        }
    }
    AddColumnIfAbsent("SealedOutbox", "SentAtUtc",
        "ALTER TABLE \"SealedOutbox\" ADD COLUMN \"SentAtUtc\" TEXT NULL");
    AddColumnIfAbsent("JoinRequests", "ConnectionId",
        "ALTER TABLE \"JoinRequests\" ADD COLUMN \"ConnectionId\" TEXT NULL");
    // The model readings table (Data/ModelReading.cs) arrived
    // after the baked demo snapshot was sealed too: a restored
    // record has every other table and not this one. It is created
    // when absent, in exactly the shape EnsureCreated gives a new
    // file, and it carries no foreign keys, so nothing else in a
    // restored record changes. Messages read before it existed
    // simply have no reading row; the pages say so plainly.
    using (var createReadings = conn.CreateCommand())
    {
        createReadings.CommandText = """
            CREATE TABLE IF NOT EXISTS "ModelReadings" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_ModelReadings" PRIMARY KEY AUTOINCREMENT,
                "HouseholdId" INTEGER NOT NULL,
                "UtteranceId" TEXT NOT NULL,
                "Channel" TEXT NOT NULL,
                "Engine" TEXT NOT NULL,
                "Model" TEXT NULL,
                "ElapsedMs" INTEGER NOT NULL,
                "PromptTokens" INTEGER NULL,
                "CompletionTokens" INTEGER NULL,
                "Outcome" TEXT NOT NULL,
                "SpeakerPersonId" INTEGER NULL,
                "Words" TEXT NULL,
                "OutputJson" TEXT NULL,
                "Reply" TEXT NULL,
                "EventIds" TEXT NULL,
                "EventCount" INTEGER NOT NULL,
                "ContextSummary" TEXT NULL,
                "ReadAtUtc" TEXT NOT NULL
            )
            """;
        createReadings.ExecuteNonQuery();
    }
    // The model calls table (Data/ModelCall.cs): the models' work that
    // is not a message reading (hearing, reviewing, seeing, drawing).
    // Same reasoning as the readings table: created when absent, in
    // the shape EnsureCreated gives a new file, no foreign keys.
    using (var createCalls = conn.CreateCommand())
    {
        createCalls.CommandText = """
            CREATE TABLE IF NOT EXISTS "ModelCalls" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_ModelCalls" PRIMARY KEY AUTOINCREMENT,
                "HouseholdId" INTEGER NOT NULL,
                "Purpose" TEXT NOT NULL,
                "Model" TEXT NOT NULL,
                "Engine" TEXT NOT NULL,
                "Outcome" TEXT NOT NULL,
                "ElapsedMs" INTEGER NOT NULL,
                "PersonId" INTEGER NULL,
                "ReadingId" INTEGER NULL,
                "DeliveryId" INTEGER NULL,
                "EventId" INTEGER NULL,
                "OutputJson" TEXT NULL,
                "Image" BLOB NULL,
                "ImageType" TEXT NULL,
                "AtUtc" TEXT NOT NULL
            )
            """;
        createCalls.ExecuteNonQuery();
    }
    conn.Close();
    DbSeeder.UseStoryDayFor(db, DateTime.UtcNow);
    DbSeeder.Seed(db);
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseStatusCodePagesWithReExecute("/NotFound");

app.UseHttpsRedirection();
app.UseRouting();
app.UseAuthorization();

// A visitor's change is any page form post; the house's own doors
// (/door, /admin) and the demo's own reset are not visitor activity.
app.Use(async (context, next) =>
{
    var path = context.Request.Path.Value ?? "/";
    if (HttpMethods.IsPost(context.Request.Method)
        && !path.StartsWith("/door", StringComparison.OrdinalIgnoreCase)
        && !path.StartsWith("/admin", StringComparison.OrdinalIgnoreCase)
        && !path.StartsWith("/demo", StringComparison.OrdinalIgnoreCase))
    {
        context.RequestServices.GetRequiredService<DemoResetService>().NoteActivity();
    }
    await next();
});

app.MapStaticAssets();
app.MapRazorPages()
   .WithStaticAssets();

// The person switcher (Services/CurrentPerson.cs): choose whose eyes
// the demo shows the house through, then go back to the page you were
// on. A person id from the household, or none for the household view.
// It sets a preference cookie only; it grants nothing, and every page
// still fences what it shows to the person chosen.
app.MapGet("/as/{personId:int?}", async (int? personId, string? @return, HttpContext http, AppDbContext db) =>
{
    // Under-18 members are never the house's users: nobody can be them.
    var valid = personId is int id && await db.Persons.AnyAsync(p => p.Id == id && !p.MinorFlag);
    CurrentPerson.Set(http, valid ? personId : null);
    return Results.LocalRedirect(CurrentPerson.SafeReturn(@return));
});

// The images the models' work is shown with. A shelf photo is the
// household's own (anyone in the house may see the shelf check); a
// picture card is shown only to someone who may see the entry it was
// drawn from, the child it was drawn for included.
app.MapGet("/models/shelf/{id:int}", async (int id, AppDbContext db) =>
{
    var call = await db.ModelCalls.FirstOrDefaultAsync(c => c.Id == id && c.Purpose == ModelCallPurpose.See);
    return call?.Image is null ? Results.NotFound() : Results.File(call.Image, call.ImageType ?? "image/jpeg");
});
app.MapGet("/models/picture/{id:int}", async (int id, HttpContext http, AppDbContext db) =>
{
    var call = await db.ModelCalls.FirstOrDefaultAsync(c => c.Id == id && c.Purpose == ModelCallPurpose.Draw);
    if (call?.Image is null || call.EventId is not int eventId)
    {
        return Results.NotFound();
    }
    var source = await db.Events.FirstOrDefaultAsync(e => e.Id == eventId);
    var viewer = CurrentPerson.FromCookie(http);
    var allowed = source is not null && (viewer is int v
        ? v == call.PersonId || BriefService.IsVisibleTo(source, v)
        : BriefService.IsVisibleWithNoReader(source));
    return allowed ? Results.File(call.Image, call.ImageType ?? "image/png") : Results.NotFound();
});

// The HTTP door adapter the stand-ins speak (the stand-ins'
// frozen surface: /door/knock, /door/card, /door/confirm,
// /door/decline, /door/status, plus the invite issue and valve
// admin the driver needs). Every act delegates to DoorService; the
// adapter adds no door logic of its own beyond identifying the
// outstanding invite by its intended person and holding the
// knock's credential verifier in process for the /door/status
// check. The check phrase ceremony, the explicit disposal, and
// standing enforcement stay exactly DoorService's.
//
// Authorization: the door has two sides, and each
// side proves itself with its own credential.
//   House side (invite, card, confirm, decline, and the /admin
//     surfaces): the caller must present the house secret in the
//     X-House-Secret header. The secret is the node's own, set
//     through the HOME_NODE_HOUSE_SECRET environment variable or
//     the Door:HouseSecret configuration value, and held only by
//     whoever operates the house's side of the door. Without it
//     configured, these endpoints refuse every call: a node with
//     no house secret has no house side, and failing closed is
//     the honest answer. The Connections page performs the same
//     ceremony, so it applies the same authorization through the
//     same HouseAuthorization check (a browser presents the
//     secret in the page's own form instead of the header): its
//     ceremony handlers refuse without it, and it never renders
//     a pending check phrase to a viewer who has not presented
//     it. Before this gate, any caller could read
//     a pending request's check phrase off /door/card and then
//     complete the inviter's confirmation step themselves, and a
//     caller who knocked with their own keypair for another
//     person and confirmed their own knock became that person's
//     newest connection, the connection their sealed deliveries
//     are addressed to.
//   Joiner side (knock, status): the knock must present the
//     invite code itself, checked against the invite's stored
//     hash before the invite is spent, and status keeps its
//     existing credential check. A knock that cannot prove the
//     code does not spend the invite.
var doorVerifiers = new System.Collections.Concurrent.ConcurrentDictionary<string, string>();
var houseAuthorization = app.Services.GetRequiredService<HouseAuthorization>();
bool HouseAuthorized(HttpRequest req) =>
    houseAuthorization.Authorizes(req.Headers["X-House-Secret"].ToString());
IResult HouseDenied() => Results.Json(
    new { error = "house authorization required" }, statusCode: 403);
// Exact name matching: a person is found by their
// full display name or their exact first name only. The prefix
// match this replaces resolved an empty inviter and the intended
// person "M" to Margaret, and ordinary unknown names on the
// confirm and knock paths surfaced as 500s where the handlers
// below now answer 400 or 404.
Person? FindPerson(AppDbContext db, string? name) =>
    DoorService.FindPersonByName(db.Persons.AsEnumerable(), name);
// The one request a connection id names: the
// request that stored the id at knock time. A pending request
// outranks a decided one and the newest outranks the older, so
// after a re-knock the card serves the CURRENT request's check
// phrase, not the previous enrollment's. Requests written before
// the id was stored on the row fall back to the party derivation,
// which names only the frozen scope parties; a generated party's
// pre-storage request cannot be found by id and stays stranded,
// exactly what the stored id fixes going forward.
JoinRequest? FindRequest(AppDbContext db, string connId, bool pendingOnly)
{
    var matches = db.JoinRequests.AsEnumerable()
        .Where(r => r.ConnectionId == connId
            || (r.ConnectionId == null
                && DoorService.ConnectionIdForParty(r.ClaimedPartyId) == connId));
    if (pendingOnly)
    {
        matches = matches.Where(r => r.Status == "Pending");
    }
    return matches
        .OrderByDescending(r => r.Status == "Pending")
        .ThenByDescending(r => r.CreatedAtUtc)
        .FirstOrDefault();
}

app.MapPost("/door/invite", async (System.Text.Json.Nodes.JsonObject body, HttpRequest req, AppDbContext db, DoorService door) =>
{
    if (!HouseAuthorized(req))
    {
        return HouseDenied();
    }
    var household = db.Households.First();
    var inviter = FindPerson(db, body["inviter"]?.GetValue<string>());
    var intended = FindPerson(db, body["intended"]?.GetValue<string>());
    if (inviter is null || intended is null)
    {
        return Results.Json(new { error = "unknown person" }, statusCode: 400);
    }
    var code = await door.CreateInviteAsync(household.Id, inviter.Id, intended.Id,
        body["level"]?.GetValue<string>() ?? "Standard");
    return Results.Json(new { code });
});

app.MapPost("/door/knock", async (System.Text.Json.Nodes.JsonObject body, AppDbContext db, DoorService door) =>
{
    var person = FindPerson(db, body["person"]?.GetValue<string>());
    if (person is null)
    {
        return Results.Json(new { error = "unknown person" }, statusCode: 403);
    }
    var invite = db.DoorInvites
        .Where(i => i.IntendedPersonId == person.Id && i.UsedAtUtc == null)
        .OrderByDescending(i => i.CreatedAtUtc)
        .FirstOrDefault();
    if (invite is null)
    {
        return Results.Json(new { error = "no matching invite" }, statusCode: 403);
    }
    // The joiner proves the invite itself: the knock carries the
    // invite code, and only its hash is compared, before the
    // invite is spent. A caller who merely knows an invite is
    // outstanding for a person can no longer knock in their place
    // or spend their invite.
    var inviteCode = body["invite_code"]?.GetValue<string>();
    if (inviteCode is null || DoorService.HashInviteCode(inviteCode) != invite.CodeHash)
    {
        return Results.Json(new { error = "invite code mismatch" }, statusCode: 403);
    }
    // Malformed key material is a bad request, never a 500
    // The fingerprint derivation validates the point.
    var publicKey = body["public_key"]?.GetValue<string>();
    string fingerprint;
    try
    {
        fingerprint = DoorService.FingerprintOf(publicKey ?? "");
    }
    catch (Exception ex) when (ex is FormatException or InvalidOperationException
            or System.Security.Cryptography.CryptographicException or ArgumentException)
    {
        return Results.Json(new { error = "malformed public key" }, statusCode: 400);
    }
    if (fingerprint != body["fingerprint"]?.GetValue<string>())
    {
        return Results.Json(new { error = "fingerprint mismatch" }, statusCode: 403);
    }
    var partyId = body["party_id"]?.GetValue<string>();
    var providerClaim = body["provider_claim"]?.GetValue<string>();
    var credentialVerifier = body["credential_verifier"]?.GetValue<string>();
    if (partyId is null || providerClaim is null || credentialVerifier is null)
    {
        return Results.Json(new { error = "knock is missing a required field" }, statusCode: 400);
    }
    JoinRequest request;
    try
    {
        request = await door.KnockOnInviteAsync(invite,
            body["person"]!.GetValue<string>(), partyId, providerClaim, publicKey!);
    }
    catch (InvalidOperationException ex)
    {
        return Results.Json(new { error = ex.Message }, statusCode: 403);
    }
    // The id the knock generated and stored on the request
    // Card, confirm, and status all read it from
    // there, so a party outside the frozen pair keeps one id
    // for the whole join.
    var connectionId = request.ConnectionId ?? DoorService.ConnectionIdForParty(request.ClaimedPartyId);
    doorVerifiers[connectionId] = credentialVerifier;
    return Results.Json(new { connection_id = connectionId, standing = "pending" });
});

app.MapGet("/door/card/{connId}", (string connId, HttpRequest req, AppDbContext db) =>
{
    if (!HouseAuthorized(req))
    {
        return HouseDenied();
    }
    var request = FindRequest(db, connId, pendingOnly: false);
    if (request is null)
    {
        return Results.Json(new { error = "unknown connection" }, statusCode: 404);
    }
    return Results.Json(new
    {
        person = request.ClaimedName,
        provider_claim = request.ProviderClaim,
        fingerprint = request.Fingerprint,
        check_phrase = request.CheckPhrase,
        standing = request.Status == "Pending" ? "pending" : request.Status.ToLowerInvariant()
    });
});

app.MapPost("/door/confirm", async (System.Text.Json.Nodes.JsonObject body, HttpRequest req, AppDbContext db, DoorService door) =>
{
    if (!HouseAuthorized(req))
    {
        return HouseDenied();
    }
    var connId = body["connection_id"]?.GetValue<string>();
    var request = connId is null ? null : FindRequest(db, connId, pendingOnly: true);
    if (request is null)
    {
        return Results.Json(new { error = "unknown connection" }, statusCode: 404);
    }
    // An unknown inviter is a bad request, never a 500.
    var inviter = FindPerson(db, body["inviter"]?.GetValue<string>());
    if (inviter is null)
    {
        return Results.Json(new { error = "unknown inviter" }, statusCode: 400);
    }
    var invite = db.DoorInvites.FirstOrDefault(i => i.Id == request.DoorInviteId);
    var person = invite?.IntendedPersonId is int intendedId
        ? db.Persons.FirstOrDefault(p => p.Id == intendedId)
        : null;
    if (person is null)
    {
        return Results.Json(new { error = "invite names no intended person" }, statusCode: 400);
    }
    try
    {
        var connection = await door.ConfirmJoinAsync(request.Id, inviter.Id, person.Id,
            body["check_phrase"]?.GetValue<string>() ?? "");
        return Results.Json(new { standing = "enrolled", connection_id = connection.ConnectionId });
    }
    catch (InvalidOperationException ex)
    {
        return Results.Json(new { error = ex.Message }, statusCode: 403);
    }
});

app.MapPost("/door/decline", async (System.Text.Json.Nodes.JsonObject body, HttpRequest req, AppDbContext db, DoorService door) =>
{
    if (!HouseAuthorized(req))
    {
        return HouseDenied();
    }
    var connId = body["connection_id"]?.GetValue<string>();
    var request = connId is null ? null : FindRequest(db, connId, pendingOnly: true);
    if (request is null)
    {
        return Results.Json(new { error = "unknown connection" }, statusCode: 404);
    }
    var inviter = FindPerson(db, body["inviter"]?.GetValue<string>());
    if (inviter is null)
    {
        return Results.Json(new { error = "unknown inviter" }, statusCode: 400);
    }
    try
    {
        await door.DeclineJoinAsync(request.Id, inviter.Id);
    }
    catch (InvalidOperationException ex)
    {
        return Results.Json(new { error = ex.Message }, statusCode: 403);
    }
    return Results.Json(new { standing = "declined" });
});

app.MapGet("/door/status/{connId}", (string connId, HttpRequest req, AppDbContext db, NodeKeyStore keys) =>
{
    var credential = req.Headers["X-Credential"].ToString();
    var verifier = Convert.ToHexStringLower(
        System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(credential)));
    if (!doorVerifiers.TryGetValue(connId, out var expected) || verifier != expected)
    {
        return Results.Json(new { error = "credential mismatch" }, statusCode: 403);
    }
    var connection = db.HouseConnections.FirstOrDefault(c => c.ConnectionId == connId);
    if (connection is not null && connection.Standing == ConnectionStanding.Active)
    {
        return Results.Json(new { standing = "enrolled", node_public_key = keys.PublicKeyBase64Url });
    }
    var request = FindRequest(db, connId, pendingOnly: false);
    return Results.Json(new
    {
        standing = request?.Status == "Declined" ? "declined" : "pending",
        node_public_key = (string?)null
    });
});

// The house's proposal to a remote member: queueing a proposal
// delivery for a Proposed event is a named node operation (the
// NodeSuite exercises SealedDeliveryService.QueueDeliveryAsync
// directly); this surface exposes that same operation so the
// house can ask an addressed remote member over the relay. The
// member's acknowledgement disposition is what disposes it.
app.MapPost("/admin/propose", async (System.Text.Json.Nodes.JsonObject body, HttpRequest req, AppDbContext db, SealedDeliveryService deliveries) =>
{
    if (!HouseAuthorized(req))
    {
        return HouseDenied();
    }
    var eventId = body["event_id"]!.GetValue<int>();
    var ev = db.Events.FirstOrDefault(e => e.Id == eventId);
    var action = db.EventActions.FirstOrDefault(a => a.EventId == eventId);
    if (ev is null || action?.TargetPersonId is null)
    {
        return Results.Json(new { error = "no addressed proposal on that event" }, statusCode: 404);
    }
    // The state and audience checks live inside the
    // operation (SealedDeliveryService.QueueProposalAsync): the
    // entry must still be a proposal, and the proposed recipient
    // must be inside the entry's stored audience, before its text
    // is sealed to anyone.
    var (item, refusal) = await deliveries.QueueProposalAsync(ev, action.TargetPersonId.Value);
    if (refusal == "not-a-proposal")
    {
        return Results.Json(new { error = "that entry is no longer a proposal" }, statusCode: 409);
    }
    if (refusal == "outside-audience")
    {
        return Results.Json(new { error = "the proposed recipient is outside that entry's stored audience" }, statusCode: 403);
    }
    if (item is null)
    {
        return Results.Json(new { error = "addressee cannot receive that delivery on their connection" }, statusCode: 409);
    }
    return Results.Json(new { packet_id = item.PacketId, status = item.Status });
});

// The valve exercise surface: standing Consent state for one
// connection and material class, following the home node's mapping. Open
// creates the Consent by read back; closed revokes any in force.
app.MapPost("/admin/valve", async (System.Text.Json.Nodes.JsonObject body, HttpRequest req, AppDbContext db, DoorService door) =>
{
    if (!HouseAuthorized(req))
    {
        return HouseDenied();
    }
    var connId = body["connection_id"]!.GetValue<string>();
    var materialClass = body["material_class"]?.GetValue<string>() ?? "OneLiner";
    var state = body["state"]!.GetValue<string>();
    var connection = db.HouseConnections.FirstOrDefault(c => c.ConnectionId == connId);
    if (connection is null)
    {
        return Results.Json(new { error = "unknown connection" }, statusCode: 404);
    }
    var inForce = db.ConsentRecords.Where(c =>
        c.PersonId == connection.PersonId && c.MaterialClass == materialClass
        && c.RevokedAtUtc == null && c.ExpiresAtUtc > DateTime.UtcNow).ToList();
    if (state == "open" && inForce.Count == 0)
    {
        await door.CreateConsentAsync(connection.HouseholdId, connection.PersonId, materialClass,
            "FreeTextOneLiners", TimeSpan.FromDays(7), createdByReadBack: true);
    }
    if (state == "closed")
    {
        foreach (var consent in inForce)
        {
            await door.RevokeConsentAsync(consent.ConsentId);
        }
    }
    return Results.Json(new { valve = state, material_class = materialClass });
});

// The demo reset (Services/DemoResetService.cs). The baseline is taken
// by the operator once the members' assistants are connected (DEPLOY.md
// step 9); a reset puts the household's content back to it and leaves
// connections and replay protection exactly as they are. Both need the
// house secret.
app.MapPost("/admin/baseline", async (HttpRequest req, DemoResetService reset) =>
{
    if (!HouseAuthorized(req))
    {
        return HouseDenied();
    }
    await reset.CaptureBaselineAsync();
    return Results.Json(new { baseline = "taken", at = DateTime.UtcNow, idle_reset_minutes = reset.IdleMinutes });
});
app.MapPost("/admin/reset", async (HttpRequest req, DemoResetService reset) =>
{
    if (!HouseAuthorized(req))
    {
        return HouseDenied();
    }
    var done = await reset.ResetAsync("admin");
    return Results.Json(new { reset = done, note = done ? "content put back to the baseline" : "no baseline yet: POST /admin/baseline first" });
});

// A visitor's "Start over" from the demo bar: the house goes back to
// its starting day (at most once every two minutes), and the visitor
// comes back to the welcome page as nobody in particular.
app.MapPost("/demo/start-over", async (HttpContext http, DemoResetService reset) =>
{
    await reset.StartOverAsync();
    CurrentPerson.Set(http, null);
    return Results.LocalRedirect("/");
});

app.Run();
return 0;
