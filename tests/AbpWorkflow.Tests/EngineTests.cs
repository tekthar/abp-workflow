using System.Reflection;
using System.Text.Json;
using Volo.Abp;

namespace AbpWorkflow.Tests;

/// <summary>Which transitions an item can take and which one a request means, and how types are named.</summary>
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
    public void A_transition_is_read_under_its_earlier_names_and_written_under_its_own()
    {
        JsonSerializerOptions json = new(JsonSerializerDefaults.Web);
        WorkflowTransition read = JsonSerializer.Deserialize<WorkflowTransition>(
            """{"number":1,"key":"done","names":{"en":"Done"},"from":["review"],"to":"done","validators":[{"type":"builtin:required"}],"postFunctions":[{"type":"builtin:set-field"}]}""", json)!;
        Assert.Equal(["builtin:required"], read.Checks.Select(u => u.Type));
        Assert.Equal(["builtin:set-field"], read.Actions.Select(u => u.Type));

        string written = JsonSerializer.Serialize(read, json);
        Assert.Contains("\"checks\":", written);
        Assert.Contains("\"actions\":", written);
        Assert.DoesNotContain("validators", written);
        Assert.DoesNotContain("postFunctions", written);
    }

    [Fact]
    public void Extensions_built_against_the_earlier_kind_names_are_read_as_checks_and_actions()
    {
        Assert.Equal(WorkflowExtensionKinds.Check, WorkflowExtensionKinds.Current("validator"));
        Assert.Equal(WorkflowExtensionKinds.Action, WorkflowExtensionKinds.Current("postFunction"));
        Assert.Equal(WorkflowExtensionKinds.Condition, WorkflowExtensionKinds.Current(WorkflowExtensionKinds.Condition));
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
        Assert.All(References(typeof(AbpWorkflowModule).Assembly),
            r => Assert.True(r == "AbpWorkflow.Contracts" || allowed.Any(a => r.StartsWith(a, StringComparison.Ordinal)), r));
    }

    [Fact]
    public void The_contracts_need_only_dotnet()
    {
        Assert.All(References(typeof(IWorkflowItemKind).Assembly), r => Assert.True(r.StartsWith("System.", StringComparison.Ordinal) || r == "netstandard", r));
    }
}
