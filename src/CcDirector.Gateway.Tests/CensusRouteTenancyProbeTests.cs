using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using CcDirector.Gateway;
using Xunit;
using Xunit.Abstractions;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The CENSUS CLOSE-OUT probes (tenant-boundary hardening, release 2026-07-31, the brief's item 5).
///
/// <see cref="ContextLessDatabaseRouteTenancyTests"/> cross-probed EIGHT context-less database routes and
/// explicitly refused to generalise to the rest. The phase-4 census re-derived the whole inventory from
/// the source and reached a written prove-or-deny verdict for every route (the table is in the phase
/// report). Two FAMILIES came out of that census as "protected by the same mechanism as the proven eight,
/// but never executed against two tenants":
///
///   1. <b>The mission notes pair</b> (<c>GET</c> and <c>PUT /gateway/missions/notes</c>) - Phase 3
///      verified them by reading the code and named a route-level probe as unfinished business. They are
///      scoped by the ambient request scope plus the entity global query filter (the architecture's good
///      seam), never by an explicit tenant argument, so only execution can say the seam is actually in
///      the path.
///   2. <b>The skills family</b> - ten context-less routes over <c>SkillStore</c>, the structural twin of
///      the workflow routes the eight-route file proved. It is the LARGEST unproven family in the census
///      and it carries an extra mechanism the workflow twin also has: the shared System library
///      partition, reachable read-only for BUILT-IN ids only (<c>SkillStore.OpenOwningContext</c>). A
///      probe that did not exercise the built-in arm would leave the interesting half untested.
///
/// MECHANICS, taken from <see cref="ContextLessDatabaseRouteTenancyTests"/>: two tenants minted through
/// the product's own hosted enrollment on ONE real hosted <see cref="GatewayHost"/>, every request over
/// real HTTP through the real auth middleware, status and media type asserted BEFORE any parse, owner
/// controls asserting the EXACT SEEDED FINGERPRINT (never a bare 200), and every refusal judged by an
/// independent RE-READ of the other tenant's state plus a destructibility control - the owner performing
/// the SAME operation successfully, so a refusal is a refusal and not an inert route.
///
/// REVERT-PROVE: drop <c>ApplyTenantScope&lt;MissionNoteEntity&gt;</c> or the three
/// <c>ApplyTenantScope&lt;Skill*Entity&gt;</c> registrations from <c>GatewayDbContext</c> and the matching
/// tests here go RED with a cross-tenant fact served.
/// </summary>
public sealed class CensusRouteTenancyProbeTests : IAsyncLifetime
{
    private const string SharedToken = "test-token";

    private readonly ITestOutputHelper _out;
    private GatewayHost _gateway = null!;
    private HttpClient _http = null!;
    private string _keyA = "";
    private string _keyB = "";

    private readonly string _instancesDir =
        Path.Combine(Path.GetTempPath(), "cc-census-" + Guid.NewGuid().ToString("N"));
    private string? _priorHosted;

    public CensusRouteTenancyProbeTests(ITestOutputHelper output) => _out = output;

    public async Task InitializeAsync()
    {
        _priorHosted = Environment.GetEnvironmentVariable("CC_GATEWAY_HOSTED");
        Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", "1");

        _gateway = new GatewayHost(port: GatewayHost.OperatingSystemAssignedPort, token: SharedToken, authEnabled: true,
            instancesDirectory: _instancesDir,
            workListsPath: Path.Combine(_instancesDir, "worklists", "worklists.json"),
            cronJobsPath: Path.Combine(_instancesDir, "cron", "cronjobs.json"),
            snoozePath: Path.Combine(_instancesDir, "snooze", "snooze.json"),
            missionsPath: Path.Combine(_instancesDir, "missions", "missions.json"),
            streamMode: true);
        await _gateway.StartAsync();
        _http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_gateway.Port}/") };

        var deviceA = HostedTestEnrollment.Enroll(_gateway, "sub-census-a", "census-a@example.com", "dev-ca", "MCA");
        var deviceB = HostedTestEnrollment.Enroll(_gateway, "sub-census-b", "census-b@example.com", "dev-cb", "MCB");
        _keyA = deviceA.DeviceKey;
        _keyB = deviceB.DeviceKey;

        Assert.True(_gateway.TenantBoundary.IsHosted, "The harness must be running the HOSTED tenant boundary.");
        Assert.NotEqual(deviceA.Tenant.Value, deviceB.Tenant.Value);
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _gateway.StopAsync();
        Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", _priorHosted);
        try { if (Directory.Exists(_instancesDir)) Directory.Delete(_instancesDir, true); }
        catch { /* best-effort */ }
    }

    // ================================================================= mission notes (mission_notes table)

    /// <summary>
    /// GET + PUT /gateway/missions/notes - both context-less (MissionNotesEndpoint.cs:29 and :34,
    /// handlers <c>()</c> and <c>(MissionNoteBody? req)</c>), scoped ONLY by the ambient request scope and
    /// the entity global query filter.
    ///
    /// The sharpest available probe: BOTH tenants write a note under the SAME mission name (the entity's
    /// composite key (TenantId, Key) makes that legal), each with a distinctive why-text. If the seam were
    /// missing, the second write would find and overwrite the first tenant's row - so this catches both a
    /// cross-tenant READ and a cross-tenant OVERWRITE, and the store's own key lookup
    /// (<c>e.Key == key</c>, unqualified by tenant) is exactly what makes the overwrite the live hazard.
    /// There is no per-mission GET, so each tenant's list is asserted as an EXACT one-element set.
    /// </summary>
    [Fact]
    public async Task MissionNotes_ContextLess_KeepEachTenantsNoteUnderTheSameMissionNameSeparate()
    {
        const string mission = "shared-mission-name";
        const string whyA = "tenant-A-why-the-census-probe";
        const string whyB = "tenant-B-why-the-census-probe";

        // SEED both, A first, under the identical mission name.
        var seedA = await Json(await Send("PUT", "gateway/missions/notes", _keyA, Body(mission, whyA)),
            HttpStatusCode.OK, "SEED    PUT /gateway/missions/notes (tenant A)");
        Assert.Equal(whyA, Str(Obj(seedA, "note"), "why"));

        var seedB = await Json(await Send("PUT", "gateway/missions/notes", _keyB, Body(mission, whyB)),
            HttpStatusCode.OK, "SEED    PUT /gateway/missions/notes (tenant B, SAME mission name)");
        Assert.Equal(whyB, Str(Obj(seedB, "note"), "why"));

        // A's list: EXACTLY its own note. Asserted as the full set with the exact seeded fingerprint, so
        // neither an extra row (B's) nor a replaced why (B's write landing on A's row) can pass.
        var listA = await Json(await Send("GET", "gateway/missions/notes", _keyA, null),
            HttpStatusCode.OK, "READ    GET /gateway/missions/notes (tenant A)");
        AssertExactlyOneNote(listA, mission, whyA);

        // B's list: EXACTLY its own note - the same mission name, its own why.
        var listB = await Json(await Send("GET", "gateway/missions/notes", _keyB, null),
            HttpStatusCode.OK, "READ    GET /gateway/missions/notes (tenant B)");
        AssertExactlyOneNote(listB, mission, whyB);

        // DESTRUCTIBILITY CONTROL: an empty why CLEARS the note (the store's documented delete path). A
        // clears its own - which must remove A's note and leave B's byte-for-byte. Without this, A's
        // one-element list above could be an inert route rather than a partitioned one.
        var clearA = await Json(await Send("PUT", "gateway/missions/notes", _keyA, Body(mission, "")),
            HttpStatusCode.OK, "CONTROL PUT /gateway/missions/notes (tenant A clears its OWN note)");
        Assert.True(Bool(clearA, "cleared"), "the owner's own clear reported nothing cleared");

        var emptiedA = await Json(await Send("GET", "gateway/missions/notes", _keyA, null),
            HttpStatusCode.OK, "AFTER   GET /gateway/missions/notes (tenant A, after its OWN clear)");
        Assert.Empty(Arr(emptiedA, "notes").EnumerateArray());

        // INDEPENDENT RE-READ: B's note survived A's clear of the same key untouched.
        var survivingB = await Json(await Send("GET", "gateway/missions/notes", _keyB, null),
            HttpStatusCode.OK, "AFTER   GET /gateway/missions/notes (tenant B, after A cleared the same key)");
        AssertExactlyOneNote(survivingB, mission, whyB);
    }

    private static void AssertExactlyOneNote(JsonElement list, string expectedMission, string expectedWhy)
    {
        var notes = Arr(list, "notes");
        Assert.Equal(1, notes.GetArrayLength());
        var only = notes[0];
        Assert.Equal(expectedMission, Str(only, "mission"));
        Assert.Equal(expectedWhy, Str(only, "why"));
    }

    private static string Body(string mission, string why)
        => JsonSerializer.Serialize(new { mission, why });

    // ================================================================= skills (skills/skill_versions/skill_files)

    /// <summary>
    /// The skills READ family, context-less: GET /gateway/skills/{id} (SkillEndpoints.cs:56), /body (:72),
    /// /files/{**filePath} (:83), /versions (:154), /versions/{version:int} (:160). Tenant B owns a
    /// published skill with a distinctive body and a distinctive supporting file; tenant A names its id on
    /// every one of the five routes.
    ///
    /// The file leg matters twice over: <c>{**filePath}</c> is a catch-all, and the census verdict is that
    /// it is an EF string comparison rather than a filesystem path - so this probe also pins that a
    /// traversal-shaped file name reaches no other tenant's content (it simply matches no row).
    /// </summary>
    [Fact]
    public async Task SkillReadFamily_ContextLess_ServesTheOwnerTheExactSeededSkill_AndIsANotFoundForTheOtherTenant()
    {
        const string sid = "bobs-skill";
        const string body = "# Bobs skill\n\nThe seeded body only tenant B may read.";
        const string fileName = "reference.md";
        const string fileContent = "tenant-B-supporting-file-content";

        await SeedPublishedSkill(_keyB, sid, body, fileName, fileContent);

        // OWNER CONTROLS: the exact seeded fingerprint on each of the five routes.
        var head = await Json(await Send("GET", $"gateway/skills/{sid}", _keyB, null),
            HttpStatusCode.OK, "CONTROL GET /gateway/skills/{id} (owner B)");
        Assert.Equal(sid, Str(head, "id"));
        Assert.Equal("Bobs skill", Str(head, "name"));

        var ownerBody = await Send("GET", $"gateway/skills/{sid}/body", _keyB, null);
        Assert.Equal(HttpStatusCode.OK, ownerBody.StatusCode);
        Assert.Contains(body, await ownerBody.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        var ownerFile = await Send("GET", $"gateway/skills/{sid}/files/{fileName}", _keyB, null);
        Assert.Equal(HttpStatusCode.OK, ownerFile.StatusCode);
        Assert.Contains(fileContent, await ownerFile.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        var versions = await Json(await Send("GET", $"gateway/skills/{sid}/versions", _keyB, null),
            HttpStatusCode.OK, "CONTROL GET /gateway/skills/{id}/versions (owner B)");
        Assert.Equal(1, Arr(versions, "versions").GetArrayLength());

        var versionOne = await Json(await Send("GET", $"gateway/skills/{sid}/versions/1", _keyB, null),
            HttpStatusCode.OK, "CONTROL GET /gateway/skills/{id}/versions/1 (owner B)");
        Assert.Equal(1, Int(versionOne, "version"));

        // CROSS: tenant A naming tenant B's skill id on every read route. Each must be an ordinary
        // not-found, and none may carry the seeded body or file content.
        foreach (var path in new[]
                 {
                     $"gateway/skills/{sid}",
                     $"gateway/skills/{sid}/body",
                     $"gateway/skills/{sid}/files/{fileName}",
                     $"gateway/skills/{sid}/versions",
                     $"gateway/skills/{sid}/versions/1",
                     // The traversal-shaped file name, on the catch-all leg: the census verdict is that
                     // this is a database key and never a path, so it must reach nothing either.
                     $"gateway/skills/{sid}/files/../../{fileName}",
                 })
        {
            var resp = await Send("GET", path, _keyA, null);
            var text = await resp.Content.ReadAsStringAsync();
            _out.WriteLine($"CROSS   GET /{path} (tenant A) -> {(int)resp.StatusCode} {text}");
            Assert.True(resp.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.BadRequest,
                $"/{path}: tenant A must be refused, got {(int)resp.StatusCode}; body was: {text}");
            Assert.DoesNotContain(body, text, StringComparison.Ordinal);
            Assert.DoesNotContain(fileContent, text, StringComparison.Ordinal);
        }

        // INDEPENDENT RE-READ: B's skill is untouched by A's attempts.
        var stillThere = await Json(await Send("GET", $"gateway/skills/{sid}", _keyB, null),
            HttpStatusCode.OK, "AFTER   GET /gateway/skills/{id} (owner B, after A's reads)");
        Assert.Equal("Bobs skill", Str(stillThere, "name"));
    }

    /// <summary>
    /// The skills WRITE family, context-less: POST /gateway/skills/{id}/publish (:126), /clone (:137),
    /// /enable (:168), /disable (:173) and DELETE /gateway/skills/{id} (:146). Tenant A names tenant B's
    /// skill id on each; every attempt must be refused, B's skill must survive each one by an independent
    /// re-read, and the OWNER must then perform the same archive successfully - the destructibility
    /// control that makes A's refusals refusals rather than an inert route.
    /// </summary>
    [Fact]
    public async Task SkillWriteFamily_ContextLess_CannotTouchTheOtherTenantsSkill_AndTheOwnerStillCan()
    {
        const string sid = "bobs-write-target";
        const string body = "# Bobs write target\n\nSeeded body.";

        await SeedPublishedSkill(_keyB, sid, body, "notes.md", "supporting");

        // CROSS: every write verb tenant A can name B's id with.
        foreach (var (method, path) in new[]
                 {
                     ("POST", $"gateway/skills/{sid}/publish"),
                     ("POST", $"gateway/skills/{sid}/enable"),
                     ("POST", $"gateway/skills/{sid}/disable"),
                     ("DELETE", $"gateway/skills/{sid}"),
                 })
        {
            var resp = await Send(method, path, _keyA, method == "POST" ? "{}" : null);
            var text = await resp.Content.ReadAsStringAsync();
            _out.WriteLine($"CROSS   {method} /{path} (tenant A) -> {(int)resp.StatusCode} {text}");
            Assert.True(resp.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.BadRequest
                            or HttpStatusCode.Conflict,
                $"{method} /{path}: tenant A must be refused, got {(int)resp.StatusCode}; body was: {text}");
        }

        // A's clone attempt of B's id: whatever it answers, it must not have produced a copy of B's
        // content in A's partition - the clone path is the one write that READS the named skill.
        var clone = await Send("POST", $"gateway/skills/{sid}/clone?newId=stolen-copy", _keyA, "{}");
        _out.WriteLine($"CROSS   POST clone (tenant A) -> {(int)clone.StatusCode} {await clone.Content.ReadAsStringAsync()}");
        var stolen = await Send("GET", "gateway/skills/stolen-copy", _keyA, null);
        var stolenText = await stolen.Content.ReadAsStringAsync();
        Assert.DoesNotContain(body, stolenText, StringComparison.Ordinal);

        // INDEPENDENT RE-READ: B's skill survived every attempt, still published, still readable.
        var survivor = await Json(await Send("GET", $"gateway/skills/{sid}", _keyB, null),
            HttpStatusCode.OK, "AFTER   GET /gateway/skills/{id} (owner B, after A's write attempts)");
        Assert.Equal(sid, Str(survivor, "id"));
        var survivorBody = await Send("GET", $"gateway/skills/{sid}/body", _keyB, null);
        Assert.Equal(HttpStatusCode.OK, survivorBody.StatusCode);
        Assert.Contains(body, await survivorBody.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        // CONTROL: the owner performs the archive A was refused, and the effect is re-read.
        var ownerArchive = await Send("DELETE", $"gateway/skills/{sid}", _keyB, null);
        Assert.Equal(HttpStatusCode.OK, ownerArchive.StatusCode);
        var gone = await Send("GET", $"gateway/skills/{sid}", _keyB, null);
        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
    }

    /// <summary>
    /// The SHARED LIBRARY arm, which the cross-tenant tests above cannot see: a BUILT-IN skill lives in
    /// the System partition and is deliberately readable by every tenant (<c>SkillStore.OpenOwningContext</c>,
    /// the named capability SharedSkillLibraryRead). This asserts the sanctioned sharing is real - both
    /// tenants read the SAME built-in - so a future change that broke it would be caught here rather than
    /// read as "isolation improved", and the census's verdict on the built-in arm is executed rather than
    /// argued.
    /// </summary>
    [Fact]
    public async Task ABuiltInSkill_IsReadableByBothTenants_TheSanctionedSharedLibrary()
    {
        var listA = await Json(await Send("GET", "gateway/skills", _keyA, null),
            HttpStatusCode.OK, "READ    GET /gateway/skills (tenant A)");
        var builtInIds = Arr(listA, "skills").EnumerateArray()
            .Where(s => s.TryGetProperty("isBuiltIn", out var b) && b.ValueKind == JsonValueKind.True)
            .Select(s => Str(s, "id"))
            .ToList();
        Assert.NotEmpty(builtInIds); // the seeded library must exist, or the arm below proves nothing
        var builtIn = builtInIds[0];

        foreach (var key in new[] { _keyA, _keyB })
        {
            var head = await Json(await Send("GET", $"gateway/skills/{builtIn}", key, null),
                HttpStatusCode.OK, $"SHARED  GET /gateway/skills/{builtIn}");
            Assert.Equal(builtIn, Str(head, "id"));

            var bodyResp = await Send("GET", $"gateway/skills/{builtIn}/body", key, null);
            Assert.Equal(HttpStatusCode.OK, bodyResp.StatusCode);
            Assert.NotEmpty(await bodyResp.Content.ReadAsStringAsync());
        }
    }

    private async Task SeedPublishedSkill(string key, string id, string body, string fileName, string fileContent)
    {
        var draft = JsonSerializer.Serialize(new
        {
            id,
            name = "Bobs skill",
            summary = "A skill owned by one tenant, named by the other.",
            triggers = new[] { "census probe" },
            bodyMarkdown = body,
            files = new[] { new { fileName, content = fileContent, encoding = "utf8" } },
            authoredBy = "census-test",
        });

        var created = await Send("POST", "gateway/skills", key, draft);
        var createdText = await created.Content.ReadAsStringAsync();
        Assert.True(created.StatusCode is HttpStatusCode.Created or HttpStatusCode.OK,
            $"SEED POST /gateway/skills failed: {(int)created.StatusCode} {createdText}");

        var published = await Send("POST", $"gateway/skills/{id}/publish", key, "{}");
        var publishedText = await published.Content.ReadAsStringAsync();
        Assert.True(published.StatusCode is HttpStatusCode.OK,
            $"SEED POST /gateway/skills/{id}/publish failed: {(int)published.StatusCode} {publishedText}");
    }

    // ================================================================= helpers (per ContextLessDatabaseRouteTenancyTests)

    /// <summary>
    /// Assert STATUS and MEDIA TYPE first, and only then parse - parsing is itself an assertion about
    /// format, and the Gateway serves a single-page-application catch-all, so a parse attempted first
    /// would launder a wrong response into a crash that says nothing about isolation.
    /// </summary>
    private async Task<JsonElement> Json(HttpResponseMessage resp, HttpStatusCode expectedStatus, string label)
    {
        var body = await resp.Content.ReadAsStringAsync();
        var mediaType = resp.Content.Headers.ContentType?.MediaType;
        _out.WriteLine($"{label} -> {(int)resp.StatusCode} {resp.StatusCode} [{mediaType}]");
        _out.WriteLine("    " + (body.Length > 600 ? body[..600] + " ...(truncated)" : body));

        Assert.True(expectedStatus == resp.StatusCode,
            $"{label}: expected HTTP {(int)expectedStatus} but got {(int)resp.StatusCode}; body was: {body}");
        Assert.True(mediaType == "application/json",
            $"{label}: expected media type application/json but got '{mediaType}'; body was: {body}");

        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private static JsonElement Member(JsonElement el, string prop, JsonValueKind expected)
    {
        Assert.True(el.ValueKind == JsonValueKind.Object,
            $"expected a JSON object to read '{prop}' from, but the value was {el.ValueKind}");
        Assert.True(el.TryGetProperty(prop, out var v), $"the response has no '{prop}' property: {el}");
        Assert.True(v.ValueKind == expected, $"'{prop}' was {v.ValueKind}, expected {expected}: {el}");
        return v;
    }

    private static string Str(JsonElement el, string prop) => Member(el, prop, JsonValueKind.String).GetString()!;

    private static int Int(JsonElement el, string prop) => Member(el, prop, JsonValueKind.Number).GetInt32();

    private static JsonElement Obj(JsonElement el, string prop) => Member(el, prop, JsonValueKind.Object);

    private static JsonElement Arr(JsonElement el, string prop) => Member(el, prop, JsonValueKind.Array);

    private static bool Bool(JsonElement el, string prop)
    {
        Assert.True(el.ValueKind == JsonValueKind.Object,
            $"expected a JSON object to read '{prop}' from, but the value was {el.ValueKind}");
        Assert.True(el.TryGetProperty(prop, out var v), $"the response has no '{prop}' property: {el}");
        Assert.True(v.ValueKind is JsonValueKind.True or JsonValueKind.False,
            $"'{prop}' was {v.ValueKind}, expected a boolean: {el}");
        return v.GetBoolean();
    }

    private Task<HttpResponseMessage> Send(string method, string path, string bearer, string? body)
    {
        var req = new HttpRequestMessage(new HttpMethod(method), path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        if (body is not null)
            req.Content = new StringContent(body, Encoding.UTF8, "application/json");
        return _http.SendAsync(req);
    }
}
