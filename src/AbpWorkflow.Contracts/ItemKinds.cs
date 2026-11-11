using System.Text.Json;

namespace AbpWorkflow;

/// <summary>
/// Items of a plugin's own that move through workflows on the host's engine (extension point
/// <c>workflow.itemKind@1</c>): test runs, contracts, purchase requests. The plugin keeps its items in its own tables
/// and answers for them through this; the host keeps the item open while it moves (its values, the status and fields
/// the workflow sets) and calls back only to read it, check a field and save it, each time in the change that moves
/// it. The kind is <c>{plugin}:{Key}</c>. Nothing here is held between calls, so the plugin can be disabled or upgraded
/// at any time: its items then wait where they are.
/// </summary>
public interface IWorkflowItemKind
{
    /// <summary>The kind's own key, <c>^[a-z][a-z0-9-]{0,31}$</c>; the host names the kind <c>{plugin}:{Key}</c>.</summary>
    string Key { get; }

    /// <summary>What one item is called in a sentence: "test run" ("3 test runs are in 'review'").</summary>
    string Noun { get; }

    /// <summary>What the items are called as a list, where an admin binds them to a workflow: "Test runs".</summary>
    string Title { get; }

    /// <summary>Where its items live: in a project (<see cref="WorkflowItemScopes.Project"/>, their scope id the project's), or the site.</summary>
    string Scope => WorkflowItemScopes.Project;

    /// <summary>Its types, each bound to a workflow of its own; at least one.</summary>
    IReadOnlyList<WorkflowItemType> Types { get; }

    /// <summary>Its values by name and shape, so a workflow's settings that read one (an approver field, a date to wait for) are checked as it's saved.</summary>
    IReadOnlyList<WorkflowItemField> Fields => [];

    /// <summary>An item as it is now, read in the caller's change; null when it's gone. Its values are its own: name, a person's id, a date.</summary>
    Task<WorkflowItemSnapshot?> OpenAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>What an account may do with the item: read it, move it, manage where it lives (and so decide its approvals as an admin). Never asked when the system acts.</summary>
    Task<WorkflowItemRights> RightsAsync(WorkflowItemSnapshot item, Guid accountId, CancellationToken cancellationToken);

    /// <summary>
    /// A field an action sets on the item (or a transition's screen): the value to store, checked as the plugin
    /// checks any write; throw <see cref="WorkflowItemException"/> to refuse it. Saved with the status in <see cref="SaveAsync"/>.
    /// </summary>
    Task<JsonElement?> CheckFieldAsync(WorkflowItemSnapshot item, string field, JsonElement? value, CancellationToken cancellationToken) =>
        throw new WorkflowItemException($"A workflow can't set '{field}' on this item.");

    /// <summary>
    /// Writes what the workflow changed (the status, the fields), in the caller's change: it commits with the engine's own
    /// rows, or neither does. Check <see cref="WorkflowItemChange.Version"/> against the stored one and bump it; a stale
    /// one is a <see cref="WorkflowItemException"/> with <c>conflict</c>.
    /// </summary>
    Task SaveAsync(WorkflowItemChange change, CancellationToken cancellationToken);

    /// <summary>How many of the items selected sit in each status (all of them when <paramref name="statuses"/> is null); statuses with none are left out.</summary>
    Task<IReadOnlyDictionary<string, int>> InStatusesAsync(WorkflowItemSelection items, IReadOnlyCollection<string>? statuses, CancellationToken cancellationToken);

    /// <summary>Binding a workflow: the items selected move from statuses it lacks to the ones an admin chose (old key to new), in the change that binds it.</summary>
    Task MoveStatusesAsync(WorkflowItemSelection items, IReadOnlyDictionary<string, string> map, Guid changeSetId, Guid actorId, CancellationToken cancellationToken);

    /// <summary>A new version of a workflow the items selected follow was published: what keeps a copy of its statuses follows it.</summary>
    Task PublishedAsync(WorkflowItemSelection items, int version, IReadOnlyList<WorkflowItemStatus> statuses, CancellationToken cancellationToken) =>
        Task.CompletedTask;
}

/// <summary>Where a kind's items live.</summary>
public static class WorkflowItemScopes
{
    /// <summary>In a project: an item's scope is its project, and a project's admins may bind its own workflow.</summary>
    public const string Project = "project";

    /// <summary>The site's: bound by the site's admins only.</summary>
    public const string Site = "site";
}

/// <summary>A type of a kind's items, which an admin binds to a workflow: "run".</summary>
public sealed record WorkflowItemType(string Key, string Title);

/// <summary>One of a kind's values: its name and shape (text, number, date, person, people, choice).</summary>
public sealed record WorkflowItemField(string Name, string Kind);

/// <summary>
/// An item as its plugin read it: what it is (the host overwrites the kind), its status, its version and its values by
/// name. A person is an account id, people a list of them, a date <c>yyyy-MM-dd</c>, as records' are. "status" and
/// "type" are the host's.
/// </summary>
public sealed record WorkflowItemSnapshot(WorkflowItemRef Item, string Status, int Version, IReadOnlyDictionary<string, JsonElement?> Values);

/// <summary>What an account may do with an item; <paramref name="WhyNot"/> says why it may not move it.</summary>
public sealed record WorkflowItemRights(bool Read, bool Move, bool ManagesScope = false, string? WhyNot = null);

/// <summary>
/// What a workflow changed on an item, to save in the caller's change: its status (from and to) and the fields its
/// actions set, the version it was read at, who acted (null for the system) and the change set it's in.
/// </summary>
public sealed record WorkflowItemChange(WorkflowItemRef Item, int Version, string FromStatus, string Status, IReadOnlyDictionary<string, JsonElement?> Fields,
    Guid? ActorId, Guid ChangeSetId);

/// <summary>
/// Items of one type: in one scope, or (scope null) everywhere but the scopes in <paramref name="ExceptScopes"/>, which
/// bind a workflow of their own.
/// </summary>
public sealed record WorkflowItemSelection(string Type, Guid? ScopeId, IReadOnlyList<Guid> ExceptScopes);

/// <summary>A status of a published workflow, as a kind keeps a copy of it: its key, names by language and category (toDo, inProgress, done).</summary>
public sealed record WorkflowItemStatus(string Key, IReadOnlyDictionary<string, string> Names, string Category);

/// <summary>A plugin's refusal of a write, in words for the person; <c>conflict</c> when someone else changed the item first.</summary>
public sealed class WorkflowItemException(string message, bool conflict = false) : Exception(message)
{
    public bool Conflict { get; } = conflict;
}

/// <summary>Naming a plugin's kinds: <c>{plugin}:{key}</c>.</summary>
public static class WorkflowItemKindNames
{
    /// <summary>The kind a plugin's <see cref="IWorkflowItemKind"/> names.</summary>
    public static string Of(string plugin, string key) => $"{plugin}:{key}";

    /// <summary>Whether a kind is a plugin's, not one of the host's own (records, assets).</summary>
    public static bool IsPlugin(string kind) => kind.Contains(':', StringComparison.Ordinal);

    /// <summary>The plugin a plugin's kind is of.</summary>
    public static string Plugin(string kind) => kind[..kind.IndexOf(':', StringComparison.Ordinal)];

    /// <summary>Whether a kind's own key is well formed: a lowercase letter, then up to 31 lowercase letters, digits and dashes.</summary>
    public static bool IsKey(string key) =>
        key.Length is > 0 and <= 32 && char.IsAsciiLetterLower(key[0]) && key.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-');
}
