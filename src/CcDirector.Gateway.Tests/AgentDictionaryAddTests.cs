using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using CcDirector.Core.Security;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Transcription;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// AN AGENT ADDS A WORD TO THE DICTATION DICTIONARY - issue #2484, the owner's ruling of 2026-08-07.
///
/// THE RULING, in the words it was given in: "yes - allow agents to add words to the dictation dictionary,
/// and do NOT put a confirmation step in the way... ADD ONLY. A session key may add a term. It may not
/// delete, rename or overwrite an existing term, and it may not touch the wrong-spellings list attached to
/// an existing term. Worst case is therefore a stray extra word, never the loss of a correction the owner
/// relies on... record which session added a term so a bad entry can be traced and swept."
///
/// WHAT WAS BROKEN. <c>SessionKeyGuard</c> allowed nothing under <c>/ingest</c>, so a session key got 403 on
/// the dictionary term endpoint and the documented "add Kubernetes to my dictionary" instruction could not
/// be carried out by any connected agent.
///
/// WHY THE PROOF IS HTTP AND HOSTED, NOT A CALL TO THE GUARD. The grant has two halves that live in two
/// files - the route is opened in <c>SessionKeyGuard</c>, and the narrowing a path cannot express (terms
/// yes, wrong spellings no) is enforced in <c>RecordingEndpoints</c>. A test against either half alone would
/// pass while the other half was wrong. And the tenancy claim - the word lands in the REQUESTING tenant's
/// glossary and nowhere else - is only true if the whole stack agrees, so it is proven on a real hosted
/// Gateway with two fully enrolled accounts, exactly as <see cref="HostedRecordingServeTests"/> proves the
/// device-key half of the same surface.
/// </summary>
[Collection("GatewayHostedMode")]
public sealed class AgentDictionaryAddTests : IAsyncLifetime
{
    private const string Token = "test-token-agent-dictionary";

    private readonly string _root;
    private readonly string? _priorRoot;
    private readonly string? _priorHosted;
    private readonly string _instancesDir =
        Path.Combine(Path.GetTempPath(), "cc-agent-dict-" + Guid.NewGuid().ToString("N"));
    private readonly string _vaultPath =
        Path.Combine(Path.GetTempPath(), "cc-agent-dict-" + Guid.NewGuid().ToString("N") + ".json");

    private GatewayHost _gateway = null!;

    // Tenant A: the account whose agent does the adding. Two clients onto it - the SESSION key an agent
    // holds, and the DEVICE key the person's Cockpit reads with (a session key cannot read the glossary,
    // which is itself part of the grant being narrow).
    private TenantId _tenantA;
    private Guid _sessionA;
    private HttpClient _agentA = null!;
    private HttpClient _personA = null!;

    // Tenant B: a second, entirely separate account. Nothing tenant A's agent does may reach it.
    private TenantId _tenantB;
    private HttpClient _personB = null!;

    public AgentDictionaryAddTests()
    {
        _priorRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        _root = Path.Combine(Path.GetTempPath(), "ccd-agent-dict-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _root);

        _priorHosted = Environment.GetEnvironmentVariable("CC_GATEWAY_HOSTED");
        Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", "1");
        Assert.True(GatewayHostedMode.IsHosted);
    }

    public async Task InitializeAsync()
    {
        _gateway = new GatewayHost(port: GatewayHost.OperatingSystemAssignedPort, token: Token, authEnabled: true,
            instancesDirectory: _instancesDir,
            keyVaultPath: _vaultPath,
            workListsPath: Path.Combine(_instancesDir, "worklists", "worklists.json"),
            snoozePath: Path.Combine(_instancesDir, "snooze", "snooze.json"),
            streamMode: true);
        await _gateway.StartAsync();

        (_tenantA, _personA) = Enrolled("dev-a", "sub-alice", "alice@example.com");
        (_tenantB, _personB) = Enrolled("dev-b", "sub-bob", "bob@example.com");

        (_sessionA, _agentA) = LiveSessionIn(_tenantA, "director-a");
    }

    /// <summary>An enrolled account, and the device-key client its person browses with.</summary>
    private (TenantId Tenant, HttpClient Http) Enrolled(string deviceId, string subject, string email)
    {
        var key = _gateway.Devices.Register(deviceId, "MA").DeviceKey;
        var tenant = _gateway.TenantRegistry.MintOrLookupBySubject(subject, email);
        _gateway.Devices.SetAccountBinding(deviceId, subject, tenant.Value);
        return (tenant, Client(key));
    }

    /// <summary>A live session inside an account, and the client an agent in it would use - the SAME shape
    /// the Director mints at launch and stamps into the session's environment as CC_GATEWAY_SESSION_KEY.</summary>
    private (Guid Session, HttpClient Http) LiveSessionIn(TenantId tenant, string directorId)
    {
        var session = Guid.NewGuid();
        var key = GatewaySessionKey.Mint();
        Assert.True(_gateway.SessionKeys.Register(
            tenant, directorId, session.ToString(), GatewaySessionKey.Hash(key), DateTime.UtcNow.AddHours(12)));
        return (session, Client(key));
    }

    private HttpClient Client(string bearer)
    {
        var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_gateway.Port}/") };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        return http;
    }

    public async Task DisposeAsync()
    {
        _agentA.Dispose();
        _personA.Dispose();
        _personB.Dispose();
        await _gateway.StopAsync();
        Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", _priorHosted);
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _priorRoot);
        try { if (File.Exists(_vaultPath)) File.Delete(_vaultPath); } catch { /* best effort */ }
        try { if (Directory.Exists(_instancesDir)) Directory.Delete(_instancesDir, true); } catch { /* best effort */ }
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { /* best effort */ }
    }

    private static StringContent Body(string json) => new(json, Encoding.UTF8, "application/json");

    private static async Task<string[]> Vocabulary(HttpClient http)
    {
        var resp = await http.GetAsync("ingest/dictionary");
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        return doc.RootElement.TryGetProperty("vocabulary", out var vocab) && vocab.ValueKind == JsonValueKind.Array
            ? vocab.EnumerateArray().Select(e => e.GetString() ?? "").ToArray()
            : Array.Empty<string>();
    }

    private static async Task<string[]> WrongSpellingsFor(HttpClient http, string term)
    {
        var resp = await http.GetAsync("ingest/dictionary");
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        if (!doc.RootElement.TryGetProperty("commonMistranscriptions", out var map)
            || map.ValueKind != JsonValueKind.Object
            || !map.TryGetProperty(term, out var list)
            || list.ValueKind != JsonValueKind.Array)
            return Array.Empty<string>();
        return list.EnumerateArray().Select(e => e.GetString() ?? "").ToArray();
    }

    // ---------- ADD SUCCEEDS ----------

    /// <summary>The whole point of the issue: an agent's session key adds a word, with nothing in the way.
    /// This is the exact call that returned 403 before.</summary>
    [Fact]
    public async Task A_session_key_adds_a_term()
    {
        var resp = await _agentA.PostAsync("ingest/dictionary/terms", Body("{\"terms\":[\"Kubernetes\"]}"));

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Contains("Kubernetes", await Vocabulary(_personA));
    }

    /// <summary>Adding is idempotent, so an agent that adds a word twice leaves ONE entry - and the entry
    /// that was already there is the one that survives. That is what "may not overwrite" means for the add
    /// verb itself: the second call changes nothing at all.</summary>
    [Fact]
    public async Task Adding_a_term_that_is_already_there_changes_nothing()
    {
        (await _agentA.PostAsync("ingest/dictionary/terms", Body("{\"terms\":[\"mindzie\"]}"))).EnsureSuccessStatusCode();
        (await _agentA.PostAsync("ingest/dictionary/terms", Body("{\"terms\":[\"MINDZIE\"]}"))).EnsureSuccessStatusCode();

        var vocabulary = await Vocabulary(_personA);
        Assert.Equal(new[] { "mindzie" }, vocabulary.Where(v => v.Equals("mindzie", StringComparison.OrdinalIgnoreCase)).ToArray());
    }

    // ---------- DELETE AND OVERWRITE ARE STILL REFUSED ----------

    /// <summary>The destructive half of the ruling. Every one of these is a way to LOSE a correction the
    /// person relies on, and every one is refused with the credential intact - 403, not 401, because the key
    /// is genuine and only the route is out of scope.</summary>
    [Theory]
    // Replacing the whole glossary - the Cockpit editor's save. It can drop a term, rename one, or empty a
    // wrong-spellings list in a single call.
    [InlineData("PUT", "ingest/dictionary")]
    // Reading it back is not part of the grant either: adding is idempotent and needs no read.
    [InlineData("GET", "ingest/dictionary")]
    // Deleting, by any spelling somebody might reach for.
    [InlineData("DELETE", "ingest/dictionary")]
    [InlineData("DELETE", "ingest/dictionary/terms")]
    [InlineData("DELETE", "ingest/dictionary/terms/mindzie")]
    // The suggestion surface: apply writes a term AND its wrong spellings; dismiss and restore change what
    // the person is shown.
    [InlineData("POST", "ingest/dictionary/suggestions/apply")]
    [InlineData("POST", "ingest/dictionary/suggestions/dismiss")]
    [InlineData("POST", "ingest/dictionary/dismissed/restore")]
    public async Task The_destructive_verbs_are_refused_for_a_session_key(string method, string path)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (method is "PUT" or "POST")
            request.Content = Body("{\"vocabulary\":[],\"commonMistranscriptions\":{},\"profiles\":{}}");

        var resp = await _agentA.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
        Assert.Contains("session_key_out_of_scope", await resp.Content.ReadAsStringAsync());
    }

    /// <summary>A refusal must leave the glossary exactly as it was. Asserting the status code alone would
    /// pass even if the handler had already written before the refusal was returned.</summary>
    [Fact]
    public async Task A_refused_replacement_leaves_the_glossary_untouched()
    {
        (await _personA.PostAsync("ingest/dictionary/terms",
            Body("{\"terms\":[\"Frederiksen\"],\"mistranscriptions\":{\"Frederiksen\":[\"Fredrickson\"]}}")))
            .EnsureSuccessStatusCode();

        var replace = await _agentA.PutAsync("ingest/dictionary",
            Body("{\"vocabulary\":[],\"commonMistranscriptions\":{},\"profiles\":{}}"));

        Assert.Equal(HttpStatusCode.Forbidden, replace.StatusCode);
        Assert.Contains("Frederiksen", await Vocabulary(_personA));
        Assert.Equal(new[] { "Fredrickson" }, await WrongSpellingsFor(_personA, "Frederiksen"));
    }

    /// <summary>The narrowing a path cannot express, and the reason it exists: a wrong-spellings entry
    /// rewrites what the transcriber HEARS, so a careless one corrupts dictation everywhere instead of
    /// leaving a stray word. The route is open to a session key; this FIELD on it is not.</summary>
    [Fact]
    public async Task A_session_key_may_not_touch_the_wrong_spellings_list()
    {
        (await _personA.PostAsync("ingest/dictionary/terms",
            Body("{\"terms\":[\"mindzie\"],\"mistranscriptions\":{\"mindzie\":[\"Mindsee\"]}}")))
            .EnsureSuccessStatusCode();

        var resp = await _agentA.PostAsync("ingest/dictionary/terms",
            Body("{\"terms\":[\"mindzie\"],\"mistranscriptions\":{\"mindzie\":[\"the\"]}}"));

        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
        Assert.Contains("session_key_add_only", await resp.Content.ReadAsStringAsync());
        Assert.Equal(new[] { "Mindsee" }, await WrongSpellingsFor(_personA, "mindzie"));
    }

    /// <summary>The person keeps the pruning shears. A term an agent added is removable through the ordinary
    /// editor save exactly like a hand-added one - the grant narrows the AGENT, never the owner.</summary>
    [Fact]
    public async Task The_person_can_still_remove_a_term_an_agent_added()
    {
        (await _agentA.PostAsync("ingest/dictionary/terms", Body("{\"terms\":[\"Kubernetes\"]}"))).EnsureSuccessStatusCode();
        Assert.Contains("Kubernetes", await Vocabulary(_personA));

        (await _personA.PutAsync("ingest/dictionary",
            Body("{\"vocabulary\":[],\"commonMistranscriptions\":{},\"profiles\":{}}"))).EnsureSuccessStatusCode();

        Assert.DoesNotContain("Kubernetes", await Vocabulary(_personA));
    }

    // ---------- THE TERM LANDS IN THE REQUESTING TENANT'S GLOSSARY AND NOWHERE ELSE ----------

    /// <summary>Tenancy. The word goes into the glossary of the account whose session key made the call, and
    /// a second account on the same Gateway never sees it - not in its glossary, and not on disk.</summary>
    [Fact]
    public async Task A_term_added_by_one_tenants_agent_reaches_no_other_tenant()
    {
        const string term = "alphaonlyagentterm";

        (await _agentA.PostAsync("ingest/dictionary/terms", Body("{\"terms\":[\"" + term + "\"]}")))
            .EnsureSuccessStatusCode();

        Assert.Contains(term, await Vocabulary(_personA));
        Assert.DoesNotContain(term, await Vocabulary(_personB));

        // And on disk: the two accounts are physically different files, and only one of them was written.
        var pathA = TenantGlossary.PathFor(_tenantA);
        var pathB = TenantGlossary.PathFor(_tenantB);
        Assert.NotEqual(pathA, pathB);
        Assert.Contains(term, File.ReadAllText(pathA));
        Assert.False(File.Exists(pathB) && File.ReadAllText(pathB).Contains(term));
    }

    // ---------- THE ADDING SESSION IS RECORDED ----------

    /// <summary>There is no confirmation step by the owner's ruling, so the trail is the only thing that
    /// lets a bad entry be traced back and swept. It names the session, the Director it belonged to, and the
    /// word - in the tenant's own partition, never anywhere shared.</summary>
    [Fact]
    public async Task The_session_that_added_a_term_is_recorded()
    {
        (await _agentA.PostAsync("ingest/dictionary/terms", Body("{\"terms\":[\"Kubernetes\",\"Helm\"]}")))
            .EnsureSuccessStatusCode();

        var trail = GlossaryAdditionLog.Read(_tenantA);

        Assert.Equal(2, trail.Count);
        Assert.All(trail, entry =>
        {
            Assert.Equal(_sessionA.ToString(), entry.SessionId);
            Assert.Equal("director-a", entry.DirectorId);
            Assert.NotEqual(default, entry.AddedAtUtc);
        });
        Assert.Equal(new[] { "Kubernetes", "Helm" }, trail.Select(e => e.Term).ToArray());

        // The trail is partitioned like the glossary it sits beside.
        Assert.Empty(GlossaryAdditionLog.Read(_tenantB));
    }

    /// <summary>A second session adding the SAME word records nothing, because it added nothing - a trail
    /// that named a session which changed no state would send whoever reads it after the wrong agent.</summary>
    [Fact]
    public async Task A_term_that_was_already_there_records_no_second_adder()
    {
        var (_, agentTwo) = LiveSessionIn(_tenantA, "director-a");
        using var second = agentTwo;

        (await _agentA.PostAsync("ingest/dictionary/terms", Body("{\"terms\":[\"Kubernetes\"]}"))).EnsureSuccessStatusCode();
        (await second.PostAsync("ingest/dictionary/terms", Body("{\"terms\":[\"Kubernetes\"]}"))).EnsureSuccessStatusCode();

        var trail = GlossaryAdditionLog.Read(_tenantA);

        Assert.Single(trail);
        Assert.Equal(_sessionA.ToString(), trail[0].SessionId);
    }

    /// <summary>A person adding through the Cockpit is not a session and leaves no session in the trail -
    /// the record exists to say WHICH AGENT, and stamping the owner's own edit with an empty one would be a
    /// false trail rather than an absent one.</summary>
    [Fact]
    public async Task A_person_adding_a_term_writes_no_session_trail()
    {
        (await _personA.PostAsync("ingest/dictionary/terms", Body("{\"terms\":[\"Kubernetes\"]}")))
            .EnsureSuccessStatusCode();

        Assert.Contains("Kubernetes", await Vocabulary(_personA));
        Assert.Empty(GlossaryAdditionLog.Read(_tenantA));
    }

    // ---------- THE TRAIL CAN BE READ, WHICH IS WHAT MAKES IT A SWEEP ----------

    /// <summary>The ruling asks that a bad entry can be traced AND SWEPT. Writing the trail satisfies only
    /// the first verb; until it can be read it is a file somebody has to find on disk. This is the read.</summary>
    [Fact]
    public async Task An_agent_can_read_back_what_agents_added()
    {
        (await _agentA.PostAsync("ingest/dictionary/terms", Body("{\"terms\":[\"Kubernetes\",\"Helm\"]}")))
            .EnsureSuccessStatusCode();

        var resp = await _agentA.GetAsync("ingest/dictionary/additions");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());

        Assert.Equal(2, doc.RootElement.GetProperty("count").GetInt32());
        var entries = doc.RootElement.GetProperty("additions").EnumerateArray().ToArray();
        // Newest first, so the batch somebody just noticed is at the top.
        Assert.Equal("Helm", entries[0].GetProperty("term").GetString());
        Assert.Equal("Kubernetes", entries[1].GetProperty("term").GetString());
        Assert.All(entries, e => Assert.Equal(_sessionA.ToString(), e.GetProperty("sessionId").GetString()));
        Assert.All(entries, e => Assert.Equal("director-a", e.GetProperty("directorId").GetString()));
    }

    /// <summary>The sweep it has to support: two sessions added words, and the trail says which was which,
    /// so ONE session's bad batch can be picked out rather than the person having to distrust the lot.</summary>
    [Fact]
    public async Task The_trail_separates_one_sessions_batch_from_anothers()
    {
        var (sessionTwo, agentTwo) = LiveSessionIn(_tenantA, "director-b");
        using var second = agentTwo;

        (await _agentA.PostAsync("ingest/dictionary/terms", Body("{\"terms\":[\"Kubernetes\"]}"))).EnsureSuccessStatusCode();
        (await second.PostAsync("ingest/dictionary/terms", Body("{\"terms\":[\"Kubernetees\",\"Kuberentes\"]}"))).EnsureSuccessStatusCode();

        var resp = await _agentA.GetAsync("ingest/dictionary/additions");
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var entries = doc.RootElement.GetProperty("additions").EnumerateArray().ToArray();

        var badBatch = entries
            .Where(e => e.GetProperty("sessionId").GetString() == sessionTwo.ToString())
            .Select(e => e.GetProperty("term").GetString())
            .OrderBy(t => t)
            .ToArray();

        Assert.Equal(new[] { "Kuberentes", "Kubernetees" }, badBatch);
        Assert.Single(entries, e => e.GetProperty("sessionId").GetString() == _sessionA.ToString());
    }

    /// <summary>The trail is partitioned like the glossary it describes: one account's agents never see
    /// another account's additions.</summary>
    [Fact]
    public async Task One_tenants_trail_is_invisible_to_another()
    {
        (await _agentA.PostAsync("ingest/dictionary/terms", Body("{\"terms\":[\"alphaonlyagentterm\"]}")))
            .EnsureSuccessStatusCode();

        var (_, agentB) = LiveSessionIn(_tenantB, "director-b");
        using var otherAccount = agentB;

        var resp = await otherAccount.GetAsync("ingest/dictionary/additions");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.DoesNotContain("alphaonlyagentterm", await resp.Content.ReadAsStringAsync());
    }

    /// <summary>Reading the trail is not reading the dictionary. The trail holds only what AGENTS wrote, so
    /// the owner's own curation stays out of reach of a session key - which is the whole reason this one
    /// read could be opened while GET /ingest/dictionary stays refused.</summary>
    [Fact]
    public async Task The_trail_does_not_expose_the_persons_own_terms()
    {
        (await _personA.PostAsync("ingest/dictionary/terms",
            Body("{\"terms\":[\"PersonsPrivateTerm\"],\"mistranscriptions\":{\"PersonsPrivateTerm\":[\"Mangled\"]}}")))
            .EnsureSuccessStatusCode();
        (await _agentA.PostAsync("ingest/dictionary/terms", Body("{\"terms\":[\"Kubernetes\"]}"))).EnsureSuccessStatusCode();

        var body = await (await _agentA.GetAsync("ingest/dictionary/additions")).Content.ReadAsStringAsync();

        Assert.Contains("Kubernetes", body);
        Assert.DoesNotContain("PersonsPrivateTerm", body);
        Assert.DoesNotContain("Mangled", body);
    }

    // ---------- A CONCURRENT HUMAN EDIT SURVIVES AN AGENT ADD, THROUGH THE REAL ENDPOINTS ----------

    /// <summary>
    /// The invariant the whole grant rests on, raced over HTTP rather than at the writer.
    /// <see cref="TenantGlossaryWriterRaceTests"/> proves the lock itself and runs in the fast suite; this
    /// proves the two ENDPOINTS actually go through it, which is the part a later edit could quietly undo
    /// by writing the file directly in a handler again.
    ///
    /// Asserts a serial ordering, not a fixed winner: the person's save may legitimately overwrite the
    /// agent's word and vice versa, but the agent's word must never survive while the person's
    /// wrong-spellings list is dropped - that is half of each write and no ordering at all.
    /// </summary>
    [Fact]
    public async Task A_persons_save_racing_an_agent_add_over_http_never_loses_the_persons_corrections()
    {
        const string curated =
            "{\"vocabulary\":[\"Frederiksen\"]," +
            "\"commonMistranscriptions\":{\"Frederiksen\":[\"Fredrickson\",\"Fredriksson\"]}," +
            "\"profiles\":{}}";

        for (var round = 0; round < 12; round++)
        {
            // A fresh account per round, so one round's file cannot carry another's state and turn a lost
            // update into a pass.
            var (tenant, person) = Enrolled($"dev-race-{round}", $"sub-race-{round}", $"race{round}@example.com");
            using var personClient = person;
            var (_, agent) = LiveSessionIn(tenant, "director-race");
            using var agentClient = agent;

            var saveTask = personClient.PutAsync("ingest/dictionary", Body(curated));
            var addTask = agentClient.PostAsync("ingest/dictionary/terms", Body("{\"terms\":[\"Kubernetes\"]}"));
            var save = await saveTask;
            var add = await addTask;

            Assert.Equal(HttpStatusCode.OK, save.StatusCode);
            Assert.Equal(HttpStatusCode.OK, add.StatusCode);

            var vocabulary = await Vocabulary(personClient);
            var wrongSpellings = await WrongSpellingsFor(personClient, "Frederiksen");

            Assert.Contains("Frederiksen", vocabulary);
            Assert.Equal(new[] { "Fredrickson", "Fredriksson" }, wrongSpellings);

            var agentWordSurvived = vocabulary.Contains("Kubernetes");
            Assert.True(
                agentWordSurvived ? vocabulary.Length == 2 : vocabulary.Length == 1,
                $"round {round}: no serial ordering produces [{string.Join(", ", vocabulary)}]");
        }
    }

    /// <summary>
    /// An add whose provenance cannot be recorded FAILS, over HTTP, and adds nothing. The endpoint used to
    /// swallow a trail-write failure and return 200, which is a silent loss of the traceability the owner
    /// traded the confirmation step for. Forced by putting a directory where the trail file belongs.
    /// </summary>
    [Fact]
    public async Task An_add_whose_provenance_cannot_be_written_fails_over_http()
    {
        Directory.CreateDirectory(GlossaryAdditionLog.PathFor(_tenantA));

        var resp = await _agentA.PostAsync("ingest/dictionary/terms", Body("{\"terms\":[\"Kubernetes\"]}"));

        Assert.Equal(HttpStatusCode.InternalServerError, resp.StatusCode);
        Assert.Contains("glossary_write_failed", await resp.Content.ReadAsStringAsync());

        // And it really added nothing - failing loudly after doing the damage would not satisfy the ruling.
        Assert.DoesNotContain("Kubernetes", await Vocabulary(_personA));
    }

    /// <summary>Every term a racing pair of sessions lands is attributable afterwards - the pair of writes
    /// is atomic through the real endpoints, not only at the writer.</summary>
    [Fact]
    public async Task Racing_sessions_over_http_keep_every_terms_provenance()
    {
        var (sessionTwo, agentTwo) = LiveSessionIn(_tenantA, "director-b");
        using var second = agentTwo;

        var one = _agentA.PostAsync("ingest/dictionary/terms", Body("{\"terms\":[\"Kubernetes\"]}"));
        var two = second.PostAsync("ingest/dictionary/terms", Body("{\"terms\":[\"Helm\"]}"));
        Assert.Equal(HttpStatusCode.OK, (await one).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await two).StatusCode);

        var vocabulary = await Vocabulary(_personA);
        var trail = GlossaryAdditionLog.Read(_tenantA);

        Assert.Contains("Kubernetes", vocabulary);
        Assert.Contains("Helm", vocabulary);
        foreach (var term in vocabulary)
            Assert.True(trail.Any(e => e.Term == term), $"'{term}' has no trail entry - it cannot be swept");
        Assert.Equal("Kubernetes", trail.Single(e => e.SessionId == _sessionA.ToString()).Term);
        Assert.Equal("Helm", trail.Single(e => e.SessionId == sessionTwo.ToString()).Term);
    }

    /// <summary>Finding is not acting. The trail read offers no way to act on what it finds - removal stays
    /// the person's, in the Cockpit editor.</summary>
    [Theory]
    [InlineData("POST", "ingest/dictionary/additions")]
    [InlineData("PUT", "ingest/dictionary/additions")]
    [InlineData("DELETE", "ingest/dictionary/additions")]
    public async Task The_trail_cannot_be_written_or_swept_by_an_agent(string method, string path)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (method is "PUT" or "POST") request.Content = Body("{}");

        var resp = await _agentA.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
        Assert.Contains("session_key_out_of_scope", await resp.Content.ReadAsStringAsync());
    }
}
