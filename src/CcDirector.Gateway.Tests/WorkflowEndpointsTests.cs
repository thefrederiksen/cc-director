using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using CcDirector.Gateway;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// End-to-end proof for the workflow catalog (issue #1617). Boots a real GatewayHost on an ephemeral
/// port with an isolated CC_DIRECTOR_ROOT and reads the routes over real HTTP, so this proves the
/// endpoint is actually mapped on the host - not merely that BuiltInWorkflows returns a list.
///
/// The point of the feature is that the Gateway is the HOME for workflows, so what is worth asserting
/// is that a client asking the Gateway gets the three shapes back, each with the seats filled in.
/// </summary>
[Collection("DirectorRoot")]
public sealed class WorkflowEndpointsTests : IAsyncLifetime
{
    private readonly string _root;
    private readonly string? _prevRoot;
    private readonly string _instancesDir =
        Path.Combine(Path.GetTempPath(), "cc-workflows-" + Guid.NewGuid().ToString("N"));

    private GatewayHost _gateway = null!;
    private HttpClient _http = null!;

    public WorkflowEndpointsTests()
    {
        _prevRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        _root = Path.Combine(Path.GetTempPath(), "ccd-workflows-test-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _root);
    }

    public async Task InitializeAsync()
    {
        _gateway = new GatewayHost(port: AllocateFreePort(), token: "test-token-12345", authEnabled: true,
            instancesDirectory: _instancesDir,
            workListsPath: Path.Combine(_instancesDir, "worklists", "worklists.json"));
        await _gateway.StartAsync();
        _http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_gateway.Port}/") };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "test-token-12345");
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _gateway.StopAsync();
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _prevRoot);
        try { if (Directory.Exists(_instancesDir)) Directory.Delete(_instancesDir, true); } catch { /* best effort */ }
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { /* best effort */ }
    }

    [Fact]
    public async Task Get_workflows_serves_the_three_shapes_of_work()
    {
        var body = await _http.GetFromJsonAsync<JsonObject>("gateway/workflows");

        var workflows = body!["workflows"]!.AsArray();
        var ids = workflows.Select(w => (string?)w!["id"]).ToArray();
        Assert.Equal(new[] { "mission", "standalone", "standalone-with-review" }, ids);
    }

    [Fact]
    public async Task Every_workflow_states_its_seats_and_what_done_means()
    {
        var body = await _http.GetFromJsonAsync<JsonObject>("gateway/workflows");

        foreach (var workflow in body!["workflows"]!.AsArray())
        {
            var name = (string?)workflow!["name"];
            Assert.False(string.IsNullOrWhiteSpace(name));
            Assert.False(string.IsNullOrWhiteSpace((string?)workflow["summary"]));
            Assert.False(string.IsNullOrWhiteSpace((string?)workflow["whenToUse"]));
            Assert.False(string.IsNullOrWhiteSpace((string?)workflow["humanCheckpoint"]));

            var steps = workflow["steps"]!.AsArray();
            Assert.NotEmpty(steps);
            foreach (var step in steps)
            {
                // A step without a doer or without a definition of done is not a workflow step, it is a
                // wish. These are the two fields the whole feature exists to make explicit.
                Assert.False(string.IsNullOrWhiteSpace((string?)step!["doer"]), $"{name}: step has no doer");
                Assert.False(string.IsNullOrWhiteSpace((string?)step["done"]), $"{name}: step has no done");
            }
        }
    }

    [Fact]
    public async Task Standalone_with_review_puts_the_review_in_a_separate_seat()
    {
        var body = await _http.GetFromJsonAsync<JsonObject>("gateway/workflows/standalone-with-review");

        var steps = body!["steps"]!.AsArray();
        Assert.Equal(2, steps.Count);
        // The whole point of this workflow versus plain Standalone: the reviewing seat is not the seat
        // that did the work. If these ever collapse to one doer, the workflow is a lie.
        Assert.Equal("Worker", (string?)steps[0]!["doer"]);
        Assert.Equal("Reviewer", (string?)steps[1]!["doer"]);
    }

    [Fact]
    public async Task Standalone_has_no_review_seat()
    {
        var body = await _http.GetFromJsonAsync<JsonObject>("gateway/workflows/standalone");

        var steps = body!["steps"]!.AsArray();
        var step = Assert.Single(steps);
        Assert.Null((string?)step!["reviewer"]);
    }

    [Fact]
    public async Task Get_workflow_by_id_is_case_insensitive()
    {
        var body = await _http.GetFromJsonAsync<JsonObject>("gateway/workflows/MISSION");
        Assert.Equal("Mission", (string?)body!["name"]);
    }

    [Fact]
    public async Task Get_unknown_workflow_reports_not_found()
    {
        // No fallback to "the first workflow" or an empty object: an unknown id is an explicit 404.
        var resp = await _http.GetAsync("gateway/workflows/does-not-exist");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task The_api_does_not_squat_on_the_cockpit_page_path()
    {
        // Regression: the catalog was first mapped at a bare /workflows, which is the path the Cockpit's
        // Workflows PAGE owns. The Gateway serves the single-page app at "/" and falls unknown page paths
        // back to index.html, so an API there WINS the path and a hard navigation to /workflows renders
        // raw JSON instead of the page. Caught by opening the page in a browser, not by a green test.
        var resp = await _http.GetAsync("workflows");
        var body = await resp.Content.ReadAsStringAsync();

        Assert.DoesNotContain("\"whenToUse\"", body);
        Assert.DoesNotContain("\"humanCheckpoint\"", body);
    }

    private static int AllocateFreePort()
    {
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
