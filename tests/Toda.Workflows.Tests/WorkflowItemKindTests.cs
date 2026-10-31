using System.Text.Json;
using Toda.Workflows;
using Microsoft.Extensions.DependencyInjection;
using Volo.Abp;

namespace Toda.Workflows.Tests;

/// <summary>
/// The engine's bridge to plugins' own items (ADR 0090): a plugin's kind is leased for each call and let go, the item
/// stays open on the host's side with copies of its values, and a kind that isn't there leaves its items waiting.
/// </summary>
public class WorkflowItemKindTests
{
    private sealed class Runs : IWorkflowItemKind
    {
        public List<WorkflowItemChange> Saved { get; } = [];

        /// <summary>The document its last answer came from, which the plugin may dispose once the call is over.</summary>
        public JsonDocument? Last { get; private set; }

        public string Key => "run";
        public string Noun => "test run";
        public string Title => "Test runs";
        public IReadOnlyList<WorkflowItemType> Types => [new("run", "Test run")];

        public Task<WorkflowItemSnapshot?> OpenAsync(Guid id, CancellationToken cancellationToken)
        {
            Last = JsonDocument.Parse("\"Smoke\"");
            return Task.FromResult<WorkflowItemSnapshot?>(new WorkflowItemSnapshot(new WorkflowItemRef("ignored", id, "TR-1", "run", Guid.NewGuid(), "QA"),
                "planned", 3, new Dictionary<string, JsonElement?> { ["name"] = Last.RootElement, ["build"] = null }));
        }

        public Task<WorkflowItemRights> RightsAsync(WorkflowItemSnapshot item, Guid accountId, CancellationToken cancellationToken) =>
            Task.FromResult(new WorkflowItemRights(Read: true, Move: false, WhyNot: "Only testers move test runs."));

        public Task<JsonElement?> CheckFieldAsync(WorkflowItemSnapshot item, string field, JsonElement? value, CancellationToken cancellationToken) =>
            field == "build" ? Task.FromResult(value) : throw new WorkflowItemException($"{field} isn't a test run's.");

        public Task SaveAsync(WorkflowItemChange change, CancellationToken cancellationToken)
        {
            Saved.Add(change);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyDictionary<string, int>> InStatusesAsync(WorkflowItemSelection items, IReadOnlyCollection<string>? statuses, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<string, int>>(new Dictionary<string, int> { ["planned"] = 2, ["running"] = 0 });

        public Task MoveStatusesAsync(WorkflowItemSelection items, IReadOnlyDictionary<string, string> map, Guid changeSetId, Guid actorId,
            CancellationToken cancellationToken) => Task.CompletedTask;
    }

    /// <summary>A plugin's kinds, leased: counts how many leases are open.</summary>
    private sealed class Source(IWorkflowItemKind? kind) : IWorkflowExtensionSource
    {
        public int Open { get; private set; }
        public int Leased { get; private set; }

        public long Generation => 1;

        public IWorkflowLease<T> Lease<T>(IServiceProvider services) where T : class
        {
            Open++;
            Leased++;
            return new Held<T>(this, kind is T found ? [found] : []);
        }

        private sealed class Held<T>(Source source, List<T> items) : IWorkflowLease<T> where T : class
        {
            public IEnumerable<(string? PluginKey, T Instance)> Items => items.Select(i => ((string?)"test-management", i));

            public ValueTask DisposeAsync()
            {
                source.Open--;
                return ValueTask.CompletedTask;
            }
        }
    }

    private static KindItems Kinds(Source source) => new(source, new ServiceCollection().AddSingleton<IWorkflowPeople, NoWorkflowPeople>().BuildServiceProvider());

    [Theory]
    [InlineData("run", true)]
    [InlineData("test-run2", true)]
    [InlineData("Run", false)]
    [InlineData("2run", false)]
    [InlineData("a-very-long-kind-key-of-thirty-three", false)]
    public void A_kinds_key_is_lowercase_letters_digits_and_dashes(string key, bool valid) => Assert.Equal(valid, WorkflowItemKindNames.IsKey(key));

    [Fact]
    public void A_plugins_kind_is_named_with_its_plugin()
    {
        Assert.Equal("test-management:run", WorkflowItemKindNames.Of("test-management", "run"));
        Assert.True(WorkflowItemKindNames.IsPlugin("test-management:run"));
        Assert.False(WorkflowItemKindNames.IsPlugin(WorkflowItemKinds.Record));
        Assert.Equal("test-management", WorkflowItemKindNames.Plugin("test-management:run"));
    }

    [Fact]
    public async Task An_item_is_open_on_the_hosts_side_with_copies_of_its_values_and_no_lease_kept()
    {
        Runs runs = new();
        Source source = new(runs);
        KindItemSession item = (await Kinds(source).OpenAsync(null!, "test-management:run", Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None))!;
        runs.Last!.Dispose();

        Assert.Equal(0, source.Open);
        Assert.Equal("test-management:run", item.Item.Kind);
        Assert.Equal("Smoke", (await item.ValuesAsync(CancellationToken.None))["name"]!.Value.GetString()); // the plugin's document is gone; this is a copy
        Assert.Equal("Only testers move test runs.", item.UnlessMayChangeStatus());
        Assert.False(item.Changed);
    }

    [Fact]
    public async Task What_the_workflow_sets_is_in_the_items_values_now_and_not_before()
    {
        Source source = new(new Runs());
        KindItemSession item = (await Kinds(source).OpenAsync(null!, "test-management:run", Guid.NewGuid(), Guid.Empty, CancellationToken.None))!;

        await item.SetFieldAsync("build", JsonSerializer.SerializeToElement("1.2"), CancellationToken.None);
        item.SetStatus(new WorkflowStatus(2, "running", new Dictionary<string, string> { ["en"] = "Running" }, StatusCategory.InProgress));

        IReadOnlyDictionary<string, JsonElement?> now = await item.ValuesAsync(CancellationToken.None);
        IReadOnlyDictionary<string, JsonElement?> before = await item.BeforeAsync(CancellationToken.None);
        Assert.Equal("1.2", now["build"]!.Value.GetString());
        Assert.Null(before["build"]);
        Assert.Equal("running", now["status"]!.Value.GetString());
        Assert.Equal("planned", before["status"]!.Value.GetString());
        Assert.Null(item.UnlessMayChangeStatus()); // the system moves it
        Assert.True(item.Changed);
        await Assert.ThrowsAsync<WorkflowFailureException>(() => item.SetFieldAsync("owner", null, CancellationToken.None));
        await Assert.ThrowsAsync<WorkflowFailureException>(() => item.SetFieldAsync("status", null, CancellationToken.None));
        Assert.Equal(0, source.Open);
    }

    [Fact]
    public async Task A_kind_that_isnt_there_leaves_its_items_waiting()
    {
        KindItems kinds = Kinds(new Source(null));
        WorkflowItemKindOffException off = await Assert.ThrowsAsync<WorkflowItemKindOffException>(() =>
            kinds.OpenAsync(null!, "test-management:run", Guid.NewGuid(), Guid.Empty, CancellationToken.None));
        Assert.Equal("test-management:run", off.Kind);
        Assert.Equal("run", await kinds.NounAsync("test-management:run"));
    }

    [Fact]
    public async Task Counts_leave_out_statuses_with_none()
    {
        Dictionary<string, int> counts = await Kinds(new Source(new Runs())).InStatusesAsync("test-management:run", new WorkflowItemSelection("run", null, []), null,
            CancellationToken.None);
        Assert.Equal(new Dictionary<string, int> { ["planned"] = 2 }, counts);
    }

    [Fact]
    public void A_plugins_refusal_is_its_words()
    {
        WorkflowItemException conflict = new("TR-1 was changed by someone else.", conflict: true);
        Assert.True(conflict.Conflict);
        Assert.IsAssignableFrom<UserFriendlyException>(new WorkflowFailureException(conflict.Message));
    }
}
