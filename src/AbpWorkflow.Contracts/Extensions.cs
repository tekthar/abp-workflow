using System.Text.Json;

namespace AbpWorkflow;

/// <summary>
/// The kinds of workflow extension: a condition decides whether a transition is offered, a check whether
/// what the person filled in lets it through, and an action does something once the item has moved.
/// </summary>
public static class WorkflowExtensionKinds
{
    public const string Condition = "condition";
    public const string Check = "check";
    public const string Action = "action";

    /// <summary>
    /// The kind under its current name: extensions built against the earlier names say "validator" for a check and
    /// "postFunction" for an action, and the engine reads them as the same.
    /// </summary>
    public static string Current(string kind) => kind switch
    {
        "validator" => Check,
        "postFunction" => Action,
        _ => kind,
    };
}

/// <summary>
/// A workflow extension (extension point <c>workflow.extension@1</c>): a condition, a check or an action. A
/// host's built-ins implement it too. Its type in a workflow is "{plugin key}:{Key}" ("builtin:…" for the host's own).
/// An extension that throws stops the transition with a message naming it; nothing is skipped.
/// </summary>
public interface IWorkflowExtension
{
    /// <summary>Unique within its plugin: lowercase letters, digits and dashes.</summary>
    string Key { get; }

    /// <summary>One of <see cref="WorkflowExtensionKinds"/>.</summary>
    string Kind { get; }

    string Title { get; }
    string Description { get; }

    /// <summary>
    /// The kinds of item it works on (<see cref="WorkflowItemKinds"/>), or null for any. One that reads records'
    /// children says "record", so it isn't offered, or published, on an asset's workflow.
    /// </summary>
    IReadOnlyList<string>? AppliesTo => null;

    /// <summary>
    /// A JSON Schema (object, properties, required, type, enum, items) for the settings. The host checks settings
    /// against it on every save and import, and generates the edit form from it when <see cref="EditComponent"/> is null.
    /// </summary>
    string SettingsSchema { get; }

    /// <summary>The plugin's own edit page (a Module Federation component), or null for the generated form.</summary>
    string? EditComponent => null;

    /// <summary>One line describing these settings, for the transition's list, history and traces.</summary>
    string Summary(JsonElement? settings, string language);

    /// <summary>
    /// The settings check: problems with settings that fit the schema but not this site (a group that doesn't exist,
    /// a field of the wrong type). Empty when they're fine.
    /// </summary>
    Task<IReadOnlyList<string>> CheckSettingsAsync(JsonElement? settings, IWorkflowSiteContext site, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<string>>([]);

    /// <summary>A condition or check: null when it holds, otherwise why not (shown to the person).</summary>
    Task<string?> EvaluateAsync(WorkflowExtensionContext context, JsonElement? settings, CancellationToken cancellationToken) =>
        Task.FromResult<string?>(null);

    /// <summary>An action: asks for changes through <see cref="WorkflowExtensionContext"/>.</summary>
    Task ExecuteAsync(WorkflowExtensionContext context, JsonElement? settings, CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>What a settings check can look up: whether groups, accounts and fields exist here.</summary>
public interface IWorkflowSiteContext
{
    /// <summary>The scope the workflow belongs to (a project, an asset schema); null for a site workflow.</summary>
    Guid? ScopeId { get; }

    /// <summary>
    /// The kind of item the workflow moves (<see cref="WorkflowItemKinds"/>), when the host knows it as the workflow is
    /// saved; null when it doesn't. A workflow isn't tied to a kind until it's bound, so binding checks it either way.
    /// </summary>
    string? ItemKind => null;

    Task<bool> GroupExistsAsync(Guid id, CancellationToken cancellationToken);
    Task<bool> AccountExistsAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>The field's value kind ("text", "number", "person", "choice"…) by its name, or null if there's none.</summary>
    Task<string?> FieldKindAsync(string field, CancellationToken cancellationToken);

    /// <summary>Whether the account exists and is a service account, not an agent's; false where the host doesn't say.</summary>
    Task<bool> ServiceAccountExistsAsync(Guid id, CancellationToken cancellationToken) => Task.FromResult(false);
}

/// <summary>
/// The item taking a transition, as an extension sees it. <see cref="Values"/> holds every field by its name as stored
/// (keys, account ids, ISO dates; declared fields as their JSON value); <see cref="Before"/> the same before the
/// transition's own fields were applied. Actions ask for changes with <see cref="SetAsync"/>; the host checks and
/// applies them like any write, in the same transaction, and records them in history. What only some hosts have (a
/// record's children, its project's lead) is asked for with <see cref="Feature{T}"/>.
/// </summary>
public abstract class WorkflowExtensionContext
{
    public required WorkflowItemRef Item { get; init; }
    public required string FromStatus { get; init; }
    public required string ToStatus { get; init; }
    public required string Transition { get; init; }
    public required Guid ActorId { get; init; }
    public required IReadOnlyDictionary<string, JsonElement?> Values { get; init; }
    public required IReadOnlyDictionary<string, JsonElement?> Before { get; init; }

    public abstract Task<IReadOnlyList<Guid>> ActorGroupIdsAsync(CancellationToken cancellationToken);

    /// <summary>Whether the person taking the transition manages the item's scope (a project's admins).</summary>
    public abstract Task<bool> ActorManagesScopeAsync(CancellationToken cancellationToken);

    /// <summary>Sets a field by its name (assignee, priority, a declared field); null clears it.</summary>
    public abstract Task SetAsync(string field, JsonElement? value, CancellationToken cancellationToken);

    /// <summary>Whether the person taking the transition holds this account's assignments here now, as their delegate.</summary>
    public virtual Task<bool> ActorHoldsAssignmentsOfAsync(Guid accountId, CancellationToken cancellationToken) => Task.FromResult(false);

    /// <summary>Something only some hosts offer for this item (a record's children and links), or null where it has none.</summary>
    public virtual T? Feature<T>() where T : class => this as T;

    /// <summary>
    /// Adds a line to this step in the workflow tracer: what was compared, what was sent. Level is "info",
    /// "warn" or "error". Does nothing when the run isn't traced; never shown to the person taking the transition.
    /// </summary>
    public virtual void Log(string message, string level = "info")
    {
    }
}
