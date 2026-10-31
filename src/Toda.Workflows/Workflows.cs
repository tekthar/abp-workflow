using System.Text.Json;
using System.Text.Json.Serialization;
using Volo.Abp.Domain.Entities;

namespace Toda.Workflows;

/// <summary>
/// A workflow (ADR 0018): statuses, transitions and their extensions, at site scope (no project) or in one project.
/// Record types are bound to one each; several types and projects can share one. <see cref="Definition"/> is the
/// published version records run on; <see cref="Draft"/> is the next one being edited.
/// </summary>
public class Workflow : Entity<Guid>
{
    protected Workflow()
    {
    }

    public Workflow(Guid id, Guid? scopeId, string key, WorkflowDefinition definition) : base(id)
    {
        ScopeId = scopeId;
        Key = key;
        Definition = definition;
        Version = 1;
        LastStatusNumber = definition.Statuses.Count == 0 ? 0 : definition.Statuses.Max(s => s.Number);
        LastTransitionNumber = definition.Transitions.Count == 0 ? 0 : definition.Transitions.Max(t => t.Number);
        CreatedAt = UpdatedAt = DateTime.UtcNow;
    }

    /// <summary>The project it belongs to; null for a site workflow (ADR 0089: the engine's scope).</summary>
    public Guid? ScopeId { get; private set; }

    /// <summary>Unique in its scope; references in configuration as code use it.</summary>
    public string Key { get; private set; } = null!;

    public Dictionary<string, string> Names { get; set; } = [];
    public string? Description { get; set; }

    /// <summary>The published version records run on.</summary>
    public WorkflowDefinition Definition { get; set; } = new();

    public int Version { get; set; }

    public WorkflowDefinition? Draft { get; set; }
    public DateTime? DraftUpdatedAt { get; set; }

    /// <summary>The highest status and transition numbers ever given, so a removed number is never reused.</summary>
    public int LastStatusNumber { get; set; }

    public int LastTransitionNumber { get; set; }

    /// <summary>
    /// The record type this workflow was made for from its status list (ADR 0018): statuses that features, imports and
    /// type settings add to the type land here.
    /// </summary>
    public Guid? GeneratedForTypeId { get; set; }

    public DateTime CreatedAt { get; private set; }
    public DateTime UpdatedAt { get; set; }

    public string Name(IReadOnlyList<string>? languages = null) => WorkflowNames.Pick(Names, Key, languages);
}

/// <summary>A published version of a workflow, kept for audit and for asynchronous work that started in it.</summary>
/// <summary>
/// A plugin's item type bound to a workflow (ADR 0090): at the site (scope null) or in one scope (a project), which
/// then has its own. Records bind through their type and assets through their schema, in their own tables; a plugin
/// can't add a column to the host's, so its bindings are the engine's.
/// </summary>
public class WorkflowBinding : Entity<Guid>
{
    protected WorkflowBinding()
    {
    }

    public WorkflowBinding(Guid id, string itemKind, string itemType, Guid? scopeId, Guid workflowId) : base(id)
    {
        ItemKind = itemKind;
        ItemType = itemType;
        ScopeId = scopeId;
        WorkflowId = workflowId;
        CreatedAt = DateTime.UtcNow;
    }

    /// <summary>The plugin's kind, <c>{plugin}:{key}</c>.</summary>
    public string ItemKind { get; private set; } = "";

    public string ItemType { get; private set; } = "";

    /// <summary>The scope that binds a workflow of its own; null for the site's binding.</summary>
    public Guid? ScopeId { get; private set; }

    public Guid WorkflowId { get; set; }

    public DateTime CreatedAt { get; private set; }
}

public class WorkflowVersion : Entity<Guid>
{
    protected WorkflowVersion()
    {
    }

    public WorkflowVersion(Guid id, Guid workflowId, int version, WorkflowDefinition definition, Guid? publishedBy) : base(id)
    {
        WorkflowId = workflowId;
        Version = version;
        Definition = definition;
        PublishedBy = publishedBy;
        PublishedAt = DateTime.UtcNow;
    }

    public Guid WorkflowId { get; private set; }
    public int Version { get; private set; }
    public WorkflowDefinition Definition { get; private set; } = new();
    public Guid? PublishedBy { get; private set; }
    public DateTime PublishedAt { get; private set; }
}

/// <summary>
/// Statuses, transitions, the status new records start in (its key; the first status when null) and whether every
/// status can reach every other without a drawn transition (as before M4).
/// </summary>
public sealed record WorkflowDefinition
{
    /// <summary>The stored JSON's format (ADR 0089): 1 when missing; a higher one is refused by this server.</summary>
    public int Format { get; init; } = Toda.Workflows.WorkflowFormat.Current;

    public List<WorkflowStatus> Statuses { get; init; } = [];
    public List<WorkflowTransition> Transitions { get; init; } = [];
    public string? Initial { get; init; }
#pragma warning disable S6964 // also the stored definition; missing, it's false, the stricter reading (only drawn transitions)
    public bool AnyToAny { get; init; }
#pragma warning restore S6964

    /// <summary>
    /// Where the designer draws the workflow: node positions and the sides transitions leave and enter by. It's the
    /// designer's own JSON, kept with each draft and version; it never changes how records move.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public System.Text.Json.Nodes.JsonObject? Diagram { get; init; }

    public WorkflowStatus? InitialStatus => Initial is null ? Statuses.FirstOrDefault() : Statuses.FirstOrDefault(s => s.Key == Initial);

    public WorkflowStatus? Status(string key) => Statuses.FirstOrDefault(s => s.Key == key);
}

/// <summary>What a status means across workflows: still to do, being worked on, or done.</summary>
public enum StatusCategory
{
    Todo,
    InProgress,
    Done,
}

/// <summary>
/// A status: a number and a key that never change once published, names per language, a category, a type (ADR 0019)
/// with its settings, and the type's exits, each an outcome ("approved") mapped to the key of the status it leads to.
/// </summary>
public sealed record WorkflowStatus(int Number, string Key, Dictionary<string, string> Names,
    [property: System.Text.Json.Serialization.JsonConverter(typeof(CamelCaseEnumConverter<StatusCategory>))] StatusCategory Category)
{
    public const string Manual = "manual";

    public string Type { get; init; } = Manual;
    public JsonElement? Settings { get; init; }
    public Dictionary<string, string>? Exits { get; init; }

    public string Name(IReadOnlyList<string>? languages = null) => WorkflowNames.Pick(Names, Key, languages);
}

/// <summary>
/// A transition: a number and key that never change once published, names, where it starts (status keys; null for a
/// global "all to one" transition), where it goes (its source for a self loop), the fields it asks for, and its
/// extensions: conditions, checks, actions (ADR 0019).
/// </summary>
public sealed record WorkflowTransition(int Number, string Key, Dictionary<string, string> Names, List<string>? From, string To)
{
    public ConditionGroup? Conditions { get; init; }
    public List<ExtensionUse> Checks { get; init; } = [];
    public List<ExtensionUse> Actions { get; init; } = [];

    /// <summary>
    /// The earlier names of <see cref="Checks"/> and <see cref="Actions"/> ("validators", "postFunctions"), read so a
    /// workflow saved or exported under them still loads whole; never written.
    /// </summary>
    [JsonInclude, JsonPropertyName("validators"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    private List<ExtensionUse>? FormerChecks { get => null; init => Checks = value ?? Checks; }

    [JsonInclude, JsonPropertyName("postFunctions"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    private List<ExtensionUse>? FormerActions { get => null; init => Actions = value ?? Actions; }

    /// <summary>Fields the person fills in when taking the transition (layout names: "resolution", "assignee", "cost").</summary>
    public List<string> Fields { get; init; } = [];

    public bool IsGlobal => From is null;

    public string Name(IReadOnlyList<string>? languages = null) => WorkflowNames.Pick(Names, Key, languages);
}

/// <summary>An enum as camelCase text in JSON ("inProgress"), in the API as in stored definitions.</summary>
public sealed class CamelCaseEnumConverter<TEnum>() : System.Text.Json.Serialization.JsonStringEnumConverter<TEnum>(JsonNamingPolicy.CamelCase)
    where TEnum : struct, Enum;

/// <summary>One use of a workflow extension: its type ("builtin:field-value", "plugin-key:quota") and settings.</summary>
public sealed record ExtensionUse(string Type, JsonElement? Settings = null);

/// <summary>Conditions that must all hold ("all") or one of which must ("any"); groups nest.</summary>
public sealed record ConditionGroup(string Match, List<ExtensionUse> Conditions, List<ConditionGroup>? Groups = null)
{
    public const string All = "all";
    public const string Any = "any";
}

/// <summary>
/// One visit of a record to an asynchronous status (ADR 0019): planned in the request that moved the record there,
/// worked on outside it (a timer, approvers), and finished by advancing the record through the outcome's exit as the
/// system. The row is also the outbox: the sweeper resumes what a crash interrupted.
/// </summary>
public class WorkflowActivity : Entity<Guid>
{
    protected WorkflowActivity()
    {
    }

    public WorkflowActivity(Guid id, Guid itemId, Guid workflowId, int workflowVersion, string statusKey, string statusType, Guid? enteredBy) : base(id)
    {
        ItemId = itemId;
        WorkflowId = workflowId;
        WorkflowVersion = workflowVersion;
        StatusKey = statusKey;
        StatusType = statusType;
        EnteredBy = enteredBy;
        EnteredAt = NextSweepAt = DateTime.UtcNow;
    }

    /// <summary>The item visiting: a record's id, or another kind's (<see cref="ItemKind"/>, ADR 0089).</summary>
    public Guid ItemId { get; private set; }

    /// <summary>What kind of item is visiting (<see cref="Toda.Workflows.WorkflowItemKinds"/>), which picks the store that moves it on.</summary>
    public string ItemKind { get; init; } = Toda.Workflows.WorkflowItemKinds.Record;

    public Guid WorkflowId { get; private set; }

    /// <summary>The workflow version the visit started in: its exits decide where the record goes.</summary>
    public int WorkflowVersion { get; private set; }

    public string StatusKey { get; private set; } = null!;
    public string StatusType { get; private set; } = null!;
    public Guid? EnteredBy { get; private set; }
    public DateTime EnteredAt { get; private set; }

    /// <summary>One of <see cref="WorkflowActivityStates"/>.</summary>
    public string State { get; set; } = WorkflowActivityStates.Pending;

    /// <summary>The exit the work came to ("approved", "done", "timedOut").</summary>
    public string? Outcome { get; set; }

    /// <summary>When the work is due: a wait's end, an approval's timeout.</summary>
    public DateTime? DueAt { get; set; }

    public DateTime? CompletedAt { get; set; }
    public DateTime? AdvancedAt { get; set; }

    /// <summary>When the sweeper may next pick the row up; pushing it forward is how a node claims it.</summary>
    public DateTime NextSweepAt { get; set; }

    public int Attempts { get; set; }
    public string? LastError { get; set; }

    /// <summary>
    /// An approval decided in another system (M9 step 4), as history: entered and decided when it was there, already moved
    /// on (Advanced), so the sweeper never acts on it; its outcome "approved" or "rejected".
    /// </summary>
    public static WorkflowActivity DecidedApproval(Guid id, Guid itemId, Guid workflowId, int workflowVersion, string statusKey, DateTime enteredAt,
        DateTime decidedAt, bool approved, IReadOnlyList<Guid> approvers) =>
        new(id, itemId, workflowId, workflowVersion, statusKey, Workflows.WorkflowCatalog.Approval, null)
        {
            EnteredAt = enteredAt,
            NextSweepAt = decidedAt,
            CompletedAt = decidedAt,
            AdvancedAt = decidedAt,
            State = WorkflowActivityStates.Advanced,
            Outcome = approved ? "approved" : "rejected",
            ApproverAccountIds = [.. approvers],
            Rule = "any",
            Required = 1,
        };

    /// <summary>Approvals: who may decide (accounts, and groups read live) and how many approvals it takes.</summary>
    public List<Guid> ApproverAccountIds { get; set; } = [];

    public List<Guid> ApproverGroupIds { get; set; } = [];
    public string? Rule { get; set; }
    public int Required { get; set; }

    /// <summary>A plugin's step (ADR 0022): what it kept while it waits, as JSON.</summary>
    public string? StateJson { get; set; }
}

public static class WorkflowActivityStates
{
    /// <summary>Waiting for its work: the time, the approvers.</summary>
    public const string Pending = "pending";

    /// <summary>The work came to an outcome; the record is owed the move through its exit.</summary>
    public const string Completed = "completed";

    /// <summary>The record moved on through the outcome's exit.</summary>
    public const string Advanced = "advanced";

    /// <summary>Timed out with no timed-out exit: the record stays and says so.</summary>
    public const string TimedOut = "timedOut";

    /// <summary>The record left the status some other way first.</summary>
    public const string Abandoned = "abandoned";

    /// <summary>Advancing kept failing; an admin needs to look (the last error says why).</summary>
    public const string Failed = "failed";
}

/// <summary>One person's decision on an approval (ADR 0019), with a comment; a project admin may decide for the approvers.</summary>
public class ApprovalDecision : Entity<Guid>, IWorkflowActed
{
    protected ApprovalDecision()
    {
    }

    public ApprovalDecision(Guid id, Guid activityId, Guid itemId, Guid accountId, bool approved, string? comment, bool asAdmin) : base(id)
    {
        ActivityId = activityId;
        ItemId = itemId;
        AccountId = accountId;
        Approved = approved;
        Comment = comment;
        AsAdmin = asAdmin;
        At = DateTime.UtcNow;
    }

    public Guid ActivityId { get; private set; }

    /// <summary>The item the approval is of (ADR 0089).</summary>
    public Guid ItemId { get; private set; }

    public Guid AccountId { get; private set; }

    /// <summary>Who acted for the account above, when that was someone else (ADR 0042).</summary>
    public Guid? ActingAccountId { get; set; }

    /// <summary>How they acted for them: impersonation or delegation.</summary>
    public string? ActingMode { get; set; }
    public bool Approved { get; private set; }
    public string? Comment { get; private set; }

    /// <summary>Decided by a project admin who isn't one of the approvers.</summary>
    public bool AsAdmin { get; private set; }

    public DateTime At { get; private set; }

    /// <summary>A decision made in another system (M9 step 4), when it was made there.</summary>
    public static ApprovalDecision Imported(Guid id, Guid activityId, Guid itemId, Guid accountId, bool approved, DateTime at) =>
        new(id, activityId, itemId, accountId, approved, null, asAdmin: false) { At = at };
}
