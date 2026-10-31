using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;

namespace Toda.Workflows.Host.Tests;

/// <summary>
/// The engine hosted alone (ADR 0089, S7), as another product hosts it: the sample host, its own documents in its own
/// table, on a database of its own, with the engine's defaults and the host's own check and status types. A memo
/// is refused by a check, sent for review through a transition's screen, decided by a group, countersigned through
/// a signal and published by a step that leaves at once; a rejection sends one back; binding a new workflow moves
/// documents by a map. Needs Postgres: localhost:5432 (postgres/postgres), or the server TODA_WORKFLOW_TEST_POSTGRES names.
/// </summary>
public sealed class WorkflowsAloneTests : IAsyncLifetime
{
    private static readonly string Server =
        Environment.GetEnvironmentVariable("TODA_WORKFLOW_TEST_POSTGRES") is { Length: > 0 } server ? server : "Host=localhost;Port=5432;Username=postgres;Password=postgres";

    // The sample's settings (appsettings.json): an admin, and reviewers b1 and b2 in group f1.
    private const string Admin = "00000000-0000-0000-0000-0000000000a1";
    private const string Reviewer = "00000000-0000-0000-0000-0000000000b1";
    private const string OtherReviewer = "00000000-0000-0000-0000-0000000000b2";
    private const string Reviewers = "00000000-0000-0000-0000-0000000000f1";
    private const string Author = "00000000-0000-0000-0000-0000000000c1";
    private const string Outsider = "00000000-0000-0000-0000-0000000000d1";

    private readonly string _database = "workflow_host_" + Guid.NewGuid().ToString("N")[..12];
    private WebApplicationFactory<Program>? _factory;

    public Task InitializeAsync()
    {
        // The host reads its settings as it builds, so they're the process's environment for this test.
        Environment.SetEnvironmentVariable("ConnectionStrings__Default", $"{Server};Database={_database}");
        _factory = new WebApplicationFactory<Program>();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (_factory is not null)
        {
            await _factory.DisposeAsync();
        }

        NpgsqlConnection.ClearAllPools();
        await using NpgsqlConnection connection = new($"{Server};Database=postgres");
        await connection.OpenAsync();
        await using NpgsqlCommand drop = new($"DROP DATABASE IF EXISTS \"{_database}\" WITH (FORCE)", connection);
        await drop.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task A_memo_is_reviewed_countersigned_and_published()
    {
        await SetUpAsync();
        JsonElement memo = await SendAsync(HttpMethod.Post, "/api/documents", Author, new { type = "memo", title = "Team lunch", body = "Pizza" });
        Assert.Equal("draft", memo.GetProperty("status").GetString());
        string doc = $"/api/documents/{memo.GetProperty("id").GetString()}";

        // Only the author may withdraw (a condition); nobody can from a draft.
        Assert.Equal(new[] { "submit" }, Keys(await SendAsync(HttpMethod.Get, $"{doc}/transitions", Outsider)));

        // The host's own check refuses a short body, and the refusal is in the runs.
        (HttpStatusCode refused, JsonElement why) = await TryAsync(HttpMethod.Post, $"{doc}/transitions/submit", Author, new { });
        Assert.Equal(HttpStatusCode.Forbidden, refused);
        Assert.Contains("needs a body of at least 10", Message(why));
        JsonElement runs = await SendAsync(HttpMethod.Get, $"{doc}/runs?all=true", Author);
        Assert.Equal("refused", runs[0].GetProperty("outcome").GetString());

        // The transition's screen fills the body; its action names the reviewer; the approval starts.
        JsonElement review = await SendAsync(HttpMethod.Post, $"{doc}/transitions/submit", Author,
            new { version = 1, fields = new { body = "Pizza on Friday at noon, the team pays." } });
        Assert.Equal("review", review.GetProperty("status").GetString());
        Assert.Equal("approval", review.GetProperty("waiting").GetProperty("type").GetString());

        // A stale version is a conflict; an outsider can neither withdraw nor decide.
        Assert.Equal(HttpStatusCode.Conflict, (await TryAsync(HttpMethod.Post, $"{doc}/transitions/withdraw", Author, new { version = 1 })).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await TryAsync(HttpMethod.Post, $"{doc}/transitions/withdraw", Outsider, new { })).Status);
        JsonElement approvals = await SendAsync(HttpMethod.Get, $"{doc}/approvals", Outsider);
        Assert.False(approvals[0].GetProperty("mayDecide").GetBoolean());
        string approval = approvals[0].GetProperty("id").GetString()!;
        Assert.Equal(HttpStatusCode.Forbidden, (await TryAsync(HttpMethod.Post, $"{doc}/approvals/{approval}", Outsider, new { approve = true })).Status);

        // A reviewer (a member of the group) approves: the memo waits for a countersignature.
        Assert.True((await SendAsync(HttpMethod.Get, $"{doc}/approvals", Reviewer))[0].GetProperty("mayDecide").GetBoolean());
        JsonElement approved = await SendAsync(HttpMethod.Post, $"{doc}/approvals/{approval}", Reviewer, new { approve = true, comment = "Fine" });
        Assert.Equal("countersign", approved.GetProperty("status").GetString());

        // The signal ends the wait; the notify step sends its message and leaves at once; the memo ends archived.
        JsonElement signed = await SendAsync(HttpMethod.Post, $"{doc}/countersign", Author, new { by = Admin });
        Assert.Equal("archived", signed.GetProperty("status").GetString());
        Assert.Contains("DOC-1: Published", (await SendAsync(HttpMethod.Get, "/api/outbox", Author)).EnumerateArray().Select(m => m.GetString()));

        // Its history, newest first: the exits that moved it, and the transition that sent it for review.
        List<string> moved = [.. (await SendAsync(HttpMethod.Get, $"{doc}/runs", Author)).EnumerateArray()
            .Select(r => $"{r.GetProperty("fromStatus").GetString()}>{r.GetProperty("endStatus").GetString()}")];
        Assert.Equal(new[] { "countersign>archived", "review>countersign", "draft>review" }, moved);
    }

    [Fact]
    public async Task A_rejection_sends_it_back_and_a_new_workflow_moves_documents_by_a_map()
    {
        await SetUpAsync();
        JsonElement memo = await SendAsync(HttpMethod.Post, "/api/documents", Author, new { type = "memo", title = "Offsite", body = "Two days by the sea, all of us." });
        string doc = $"/api/documents/{memo.GetProperty("id").GetString()}";
        await SendAsync(HttpMethod.Post, $"{doc}/transitions/submit", Author, new { });
        string approval = (await SendAsync(HttpMethod.Get, $"{doc}/approvals", OtherReviewer))[0].GetProperty("id").GetString()!;
        JsonElement back = await SendAsync(HttpMethod.Post, $"{doc}/approvals/{approval}", OtherReviewer, new { approve = false, comment = "Too long" });
        Assert.Equal("draft", back.GetProperty("status").GetString());

        // A workflow without "draft": binding it asks for a map, then moves the memo.
        await SendAsync(HttpMethod.Post, "/api/workflows", Admin, new
        {
            key = "simple",
            definition = new
            {
                statuses = new object[] { Status("open", "todo"), Status("closed", "done") },
                transitions = new object[] { Transition("close", ["open"], "closed") },
            },
        });
        JsonElement preview = await SendAsync(HttpMethod.Put, "/api/document-types/memo/workflow", Admin, new { workflow = "simple", apply = false });
        Assert.Equal(1, preview.GetProperty("stranded").GetProperty("draft").GetInt32());
        Assert.Equal(HttpStatusCode.Forbidden, (await TryAsync(HttpMethod.Put, "/api/document-types/memo/workflow", Admin, new { workflow = "simple" })).Status);
        await SendAsync(HttpMethod.Put, "/api/document-types/memo/workflow", Admin, new { workflow = "simple", map = new { draft = "open" } });
        JsonElement moved = await SendAsync(HttpMethod.Get, doc, Author);
        Assert.Equal("open", moved.GetProperty("status").GetString());
        Assert.Equal("simple", moved.GetProperty("workflow").GetString());
    }

    /// <summary>The memo workflow: draft → review (an approval by the reviewers) → countersign (waits for a signal) → published (notifies) → archived.</summary>
    private async Task SetUpAsync()
    {
        await SendAsync(HttpMethod.Post, "/api/workflows", Admin, new
        {
            key = "memo-review",
            names = new { en = "Memo review" },
            definition = new
            {
                statuses = new object[]
                {
                    Status("draft", "todo"),
                    Status("review", "inProgress", "approval", new { groups = new[] { Reviewers }, rule = "any" }, new { approved = "countersign", rejected = "draft" }),
                    Status("countersign", "inProgress", "builtin:countersign", new { }, new { signed = "published" }),
                    Status("published", "done", "builtin:notify", new { message = "Published" }, new { done = "archived" }),
                    Status("archived", "done"),
                },
                transitions = new object[]
                {
                    Transition("submit", ["draft"], "review") with
                    {
                        Fields = ["body"],
                        Checks = [new("builtin:has-body", JsonSerializer.SerializeToElement(new { minLength = 10 }))],
                        Actions = [new("builtin:set-field", JsonSerializer.SerializeToElement(new { field = "reviewer", value = Reviewer }))],
                    },
                    Transition("withdraw", ["review"], "draft") with
                    {
                        Conditions = new("all", [new("builtin:person", JsonSerializer.SerializeToElement(new { accounts = new[] { Author } }))]),
                    },
                },
            },
        });
        JsonElement bound = await SendAsync(HttpMethod.Put, "/api/document-types/memo/workflow", Admin, new { workflow = "memo-review" });
        Assert.True(bound.GetProperty("applied").GetBoolean());
    }

    private static object Status(string key, string category, string type = "manual", object? settings = null, object? exits = null) =>
        new { number = 0, key, names = new { en = key }, category, type, settings, exits };

    private static WorkflowTransition Transition(string key, List<string> from, string to) =>
        new(0, key, new Dictionary<string, string> { ["en"] = key }, from, to);

    private static List<string> Keys(JsonElement transitions) => [.. transitions.EnumerateArray().Select(t => t.GetProperty("key").GetString()!)];

    private static string Message(JsonElement error) => error.GetProperty("error").GetProperty("message").GetString()!;

    private async Task<JsonElement> SendAsync(HttpMethod method, string path, string account, object? body = null)
    {
        (HttpStatusCode status, JsonElement json) = await TryAsync(method, path, account, body);
        Assert.True(status == HttpStatusCode.OK, $"{method} {path}: {(int)status} {json}");
        return json;
    }

    private async Task<(HttpStatusCode Status, JsonElement Json)> TryAsync(HttpMethod method, string path, string account, object? body = null)
    {
        using HttpRequestMessage request = new(method, path);
        request.Headers.Add("X-Account", account);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: new JsonSerializerOptions(JsonSerializerDefaults.Web));
        }

        using HttpResponseMessage response = await _factory!.CreateClient().SendAsync(request);
        string text = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone());
    }
}
