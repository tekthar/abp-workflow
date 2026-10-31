using System.Reflection;
using Volo.Abp;

namespace Toda.Workflows.Tests;

/// <summary>Which transitions an item can take and which one a request means (ADR 0018), and how types are named.</summary>
public class EngineTests
{
    private static WorkflowStatus Status(string key, StatusCategory category = StatusCategory.Todo) =>
        new(0, key, new Dictionary<string, string> { ["en"] = key }, category);

    private static WorkflowTransition Transition(string key, List<string>? from, string to) =>
        new(1, key, new Dictionary<string, string> { ["en"] = key }, from, to);

    private static readonly WorkflowDefinition Drawn = new()
    {
        Statuses = [Status("draft"), Status("review", StatusCategory.InProgress), Status("done", StatusCategory.Done)],
        Transitions = [Transition("submit", ["draft"], "review"), Transition("approve", ["review"], "done"), Transition("reopen", null, "draft")],
    };

    [Fact]
    public void Only_drawn_transitions_leave_a_status_without_any_to_any()
    {
        Assert.Equal(["submit"], WorkflowEngine.Available(Drawn, "draft").Select(t => t.Key));
        // A global transition leaves every status but its own target.
        Assert.Equal(["approve", "reopen"], WorkflowEngine.Available(Drawn, "review").Select(t => t.Key));
    }

    [Fact]
    public void Any_to_any_adds_an_implicit_way_to_each_status_no_drawn_one_reaches()
    {
        WorkflowDefinition open = Drawn with { AnyToAny = true };
        List<WorkflowTransition> fromDraft = WorkflowEngine.Available(open, "draft");
        Assert.Equal(["submit", "to:done"], fromDraft.Select(t => t.Key));
        Assert.True(WorkflowEngine.IsImplicit(fromDraft[1]));
    }

    [Fact]
    public void A_request_means_a_transition_by_key_or_the_one_way_to_a_status()
    {
        Assert.Equal("submit", WorkflowEngine.Resolve(Drawn, "draft", "submit", null, "Memo")!.Key);
        Assert.Equal("submit", WorkflowEngine.Resolve(Drawn, "draft", null, "review", "Memo")!.Key);
        Assert.Null(WorkflowEngine.Resolve(Drawn, "draft", null, "draft", "Memo"));
        UserFriendlyException wrong = Assert.Throws<UserFriendlyException>(() => WorkflowEngine.Resolve(Drawn, "draft", "approve", null, "Memo"));
        Assert.Contains("From here: submit (to review)", wrong.Message);
        Assert.Throws<UserFriendlyException>(() => WorkflowEngine.Resolve(Drawn, "draft", null, "done", "Memo"));
    }

    [Fact]
    public void A_hosts_own_types_are_builtin_and_a_plugins_carry_its_key()
    {
        Assert.Equal("builtin:has-body", WorkflowCatalog.TypeName(null, "has-body"));
        Assert.Equal("crm:quote", WorkflowCatalog.TypeName("crm", "quote"));
        Assert.True(WorkflowCatalog.IsPluginType("builtin:notify"));
        Assert.False(WorkflowCatalog.IsPluginType(WorkflowCatalog.Approval));
    }
}

/// <summary>The engine and its contracts stand alone: no host's code in them, and the contracts not even ABP or EF Core.</summary>
public class ArchitectureTests
{
    private static IEnumerable<string> References(Assembly assembly) => assembly.GetReferencedAssemblies().Select(a => a.Name!);

    [Fact]
    public void The_engine_needs_only_dotnet_abp_ef_core_npgsql_and_its_contracts()
    {
        string[] allowed = ["System.", "Microsoft.", "Volo.Abp.", "Npgsql", "netstandard"];
        Assert.All(References(typeof(TodaWorkflowsModule).Assembly),
            r => Assert.True(r == "Toda.Workflows.Contracts" || allowed.Any(a => r.StartsWith(a, StringComparison.Ordinal)), r));
    }

    [Fact]
    public void The_contracts_need_only_dotnet()
    {
        Assert.All(References(typeof(IWorkflowItemKind).Assembly), r => Assert.True(r.StartsWith("System.", StringComparison.Ordinal) || r == "netstandard", r));
    }
}
