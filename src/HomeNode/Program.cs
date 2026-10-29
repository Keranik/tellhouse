using HomeNode.Data;
using HomeNode.Services;
using HomeNode.Services.Extraction;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

// The local demo suite runs the whole pipeline against a scratch
// slice and exits; it never starts the web host or touches the demo
// database. See Services/DemoSuite.cs. It is the node's regression
// harness: the monolith's 103 checks, assertions unmodified,
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
builder.Services.AddScoped<IExtractionClient>(sp =>
{
    var options = sp.GetRequiredService<IOptions<ExtractionOptions>>().Value;
    return options.Mode.Equals("Live", StringComparison.OrdinalIgnoreCase)
        ? ActivatorUtilities.CreateInstance<MetaExtractionClient>(sp)
        : new MockExtractionClient();
});
builder.Services.AddScoped<ExtractionContextBuilder>();
builder.Services.AddScoped<ExtractionService>();
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
{ Timeout = TimeSpan.FromSeconds(10) });
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

// Integration: the relay loop runs only when a relay is configured
// (Relay:BaseUrl). It polls the relay, feeds sealed intake, and
// posts the sealed outbox. See Services/RelayLoopService.cs.
if (!string.IsNullOrWhiteSpace(builder.Configuration.GetValue<string>("Relay:BaseUrl")))
{
    builder.Services.AddHostedService<RelayLoopService>();
}

// The one timer in the system: it runs the 24-hour lapse on
// unanswered proposals (see Services/LapseService.cs).
builder.Services.AddHostedService<LapseService>();

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
    DbSeeder.Seed(db);
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseRouting();
app.UseAuthorization();

app.MapStaticAssets();
app.MapRazorPages()
   .WithStaticAssets();

// The HTTP door adapter the stand-ins speak (their
// frozen surface: /door/knock, /door/card, /door/confirm,
// /door/decline, /door/status, plus the invite issue and valve
// admin the driver needs). Every act delegates to DoorService; the
// adapter adds no door logic of its own beyond identifying the
// outstanding invite by its intended person (the frozen knock body
// carries no invite code field) and holding the knock's credential
// verifier in process for the /door/status check. The check phrase
// ceremony, the explicit disposal, and standing enforcement stay
// exactly DoorService's.
var doorVerifiers = new System.Collections.Concurrent.ConcurrentDictionary<string, string>();
Person? FindPerson(AppDbContext db, string firstName) =>
    db.Persons.AsEnumerable().FirstOrDefault(p =>
        p.DisplayName.StartsWith(firstName, StringComparison.OrdinalIgnoreCase));

app.MapPost("/door/invite", async (System.Text.Json.Nodes.JsonObject body, AppDbContext db, DoorService door) =>
{
    var household = db.Households.First();
    var inviter = FindPerson(db, body["inviter"]!.GetValue<string>());
    var intended = FindPerson(db, body["intended"]!.GetValue<string>());
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
    var person = FindPerson(db, body["person"]!.GetValue<string>());
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
    var fingerprint = DoorService.FingerprintOf(body["public_key"]!.GetValue<string>());
    if (fingerprint != body["fingerprint"]!.GetValue<string>())
    {
        return Results.Json(new { error = "fingerprint mismatch" }, statusCode: 403);
    }
    var request = await door.KnockOnInviteAsync(invite,
        body["person"]!.GetValue<string>(), body["party_id"]!.GetValue<string>(),
        body["provider_claim"]!.GetValue<string>(), body["public_key"]!.GetValue<string>());
    var connectionId = DoorService.ConnectionIdForParty(request.ClaimedPartyId);
    doorVerifiers[connectionId] = body["credential_verifier"]!.GetValue<string>();
    return Results.Json(new { connection_id = connectionId, standing = "pending" });
});

app.MapGet("/door/card/{connId}", (string connId, AppDbContext db) =>
{
    var request = db.JoinRequests.AsEnumerable()
        .FirstOrDefault(r => DoorService.ConnectionIdForParty(r.ClaimedPartyId) == connId);
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

app.MapPost("/door/confirm", async (System.Text.Json.Nodes.JsonObject body, AppDbContext db, DoorService door) =>
{
    var connId = body["connection_id"]!.GetValue<string>();
    var request = db.JoinRequests.AsEnumerable()
        .FirstOrDefault(r => DoorService.ConnectionIdForParty(r.ClaimedPartyId) == connId && r.Status == "Pending");
    if (request is null)
    {
        return Results.Json(new { error = "unknown connection" }, statusCode: 404);
    }
    var inviter = FindPerson(db, body["inviter"]!.GetValue<string>());
    var invite = db.DoorInvites.First(i => i.Id == request.DoorInviteId);
    var person = db.Persons.First(p => p.Id == invite.IntendedPersonId);
    try
    {
        var connection = await door.ConfirmJoinAsync(request.Id, inviter!.Id, person.Id,
            body["check_phrase"]!.GetValue<string>());
        return Results.Json(new { standing = "enrolled", connection_id = connection.ConnectionId });
    }
    catch (InvalidOperationException ex)
    {
        return Results.Json(new { error = ex.Message }, statusCode: 403);
    }
});

app.MapPost("/door/decline", async (System.Text.Json.Nodes.JsonObject body, AppDbContext db, DoorService door) =>
{
    var connId = body["connection_id"]!.GetValue<string>();
    var request = db.JoinRequests.AsEnumerable()
        .FirstOrDefault(r => DoorService.ConnectionIdForParty(r.ClaimedPartyId) == connId && r.Status == "Pending");
    if (request is null)
    {
        return Results.Json(new { error = "unknown connection" }, statusCode: 404);
    }
    var inviter = FindPerson(db, body["inviter"]!.GetValue<string>());
    await door.DeclineJoinAsync(request.Id, inviter!.Id);
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
    var request = db.JoinRequests.AsEnumerable()
        .FirstOrDefault(r => DoorService.ConnectionIdForParty(r.ClaimedPartyId) == connId);
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
app.MapPost("/admin/propose", async (System.Text.Json.Nodes.JsonObject body, AppDbContext db, SealedDeliveryService deliveries) =>
{
    var eventId = body["event_id"]!.GetValue<int>();
    var ev = db.Events.FirstOrDefault(e => e.Id == eventId);
    var action = db.EventActions.FirstOrDefault(a => a.EventId == eventId);
    if (ev is null || action?.TargetPersonId is null)
    {
        return Results.Json(new { error = "no addressed proposal on that event" }, statusCode: 404);
    }
    var item = await deliveries.QueueDeliveryAsync(ev.HouseholdId, action.TargetPersonId.Value,
        ev.Body, "Proposal", sourceEventId: ev.Id, kind: "proposal");
    if (item is null)
    {
        return Results.Json(new { error = "addressee holds no Active connection" }, statusCode: 409);
    }
    return Results.Json(new { packet_id = item.PacketId, status = item.Status });
});

// The valve exercise surface: standing Consent state for one
// connection and material class, following the home node's mapping. Open
// creates the Consent by read back; closed revokes any in force.
app.MapPost("/admin/valve", async (System.Text.Json.Nodes.JsonObject body, AppDbContext db, DoorService door) =>
{
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

app.Run();
return 0;
