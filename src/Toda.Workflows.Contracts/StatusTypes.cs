using System.Text.Json;

namespace Toda.Workflows;

/// <summary>How a status type runs (ADR 0022).</summary>
public static class StatusTypeModes
{
    /// <summary>Leaves at once, inside the request that brought the item in.</summary>
    public const string Sync = "sync";

    /// <summary>Waits: for a time, a signal (a reply arrives), or both; the worker resumes it.</summary>
    public const string Async = "async";
}

/// <summary>
/// A status type from a plugin ("workflow.statusType@1", ADR 0022), keyed "{plugin}:{key}" in workflows. A sync type
/// returns its exit from <see cref="EnterAsync"/>. An async type plans its work there and says what it waits for; the
/// worker later calls <see cref="ResumeAsync"/> when the time comes or a signal arrives (<see cref="IWorkflowSignals"/>),
/// and it answers with an exit or "keep waiting". The engine owns everything else: leases, retries, the hop limit,
/// moving the item. A type never writes the item. If its plugin is off, an item in its status stays and says so, and
/// goes on from where it was when the plugin is back.
/// </summary>
public interface IWorkflowStatusType
{
    /// <summary>Unique within its plugin: lowercase letters, digits and dashes.</summary>
    string Key { get; }

    /// <summary>One of <see cref="StatusTypeModes"/>.</summary>
    string Mode { get; }

    /// <summary>The exits a status of this type must map to statuses.</summary>
    IReadOnlyList<string> Exits { get; }

    /// <summary>Exits it may map ("timedOut"); one that isn't mapped leaves the item where it is, saying so.</summary>
    IReadOnlyList<string> OptionalExits => [];

    /// <summary>
    /// The settings property that names more exits, each of which a status must map: a list of exit names, or of objects
    /// with an <c>exit</c> (as a decision's routes are). An AI step's routes, say. Null when the settings name none.
    /// </summary>
    string? RoutesSetting => null;

    /// <summary>The kinds of item it works on (<see cref="WorkflowItemKinds"/>), or null for any.</summary>
    IReadOnlyList<string>? AppliesTo => null;

    string Title { get; }
    string Description { get; }

    /// <summary>A JSON Schema for the settings, checked on every save and import; the designer's form is made from it.</summary>
    string SettingsSchema { get; }

    /// <summary>The plugin's own edit component, or null for the generated form.</summary>
    string? EditComponent => null;

    /// <summary>
    /// The plugin's own component shown with a step that waits in a status of this type (ADR 0081): on the item's page,
    /// and on a request's page in the portal, to whoever sees the item there. It's given the item's key, the step's
    /// activity id, the status's key, where it shows (<c>record</c> or <c>portal</c>) and the SDK, and calls the plugin's
    /// own endpoints, which check who may do what. Null for none.
    /// </summary>
    string? Panel => null;

    /// <summary>One line describing these settings.</summary>
    string Summary(JsonElement? settings, string language) => Title;

    /// <summary>Problems with settings that fit the schema but not this site; empty when they're fine.</summary>
    Task<IReadOnlyList<string>> CheckSettingsAsync(JsonElement? settings, IWorkflowSiteContext site, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<string>>([]);

    /// <summary>The item has just come into a status of this type.</summary>
    Task<StatusEntryResult> EnterAsync(WorkflowStatusContext context, JsonElement? settings, CancellationToken cancellationToken);

    /// <summary>
    /// An async type's time came (<paramref name="signal"/> null) or a signal arrived for it: an exit, or keep waiting.
    /// </summary>
    Task<StatusResumeResult> ResumeAsync(WorkflowStatusContext context, JsonElement? settings, JsonElement? signal, CancellationToken cancellationToken) =>
        Task.FromResult(StatusResumeResult.KeepWaiting(null));
}

/// <summary>What entering did: leave by an exit now (sync), or wait until a time (or none: only a signal ends it), with state kept for later.</summary>
public sealed record StatusEntryResult(string? Exit, DateTime? WaitUntil = null, JsonElement? State = null)
{
    public static StatusEntryResult Leave(string exit) => new(exit);

    public static StatusEntryResult Wait(DateTime? until, JsonElement? state = null) => new(null, until, state);
}

/// <summary>What resuming did: leave by an exit, or keep waiting (until a new time, with new state).</summary>
public sealed record StatusResumeResult(string? Exit, DateTime? WaitUntil = null, JsonElement? State = null)
{
    public static StatusResumeResult Leave(string exit) => new(exit);

    public static StatusResumeResult KeepWaiting(DateTime? until, JsonElement? state = null) => new(null, until, state);
}

/// <summary>
/// The item in a status of a plugin's type, as the type sees it: what it is, its values by name (as stored), the
/// step's state (what <see cref="StatusEntryResult.State"/> kept). What only some hosts have (mailing a request's
/// customers) is asked for with <see cref="Feature{T}"/>.
/// </summary>
public abstract class WorkflowStatusContext
{
    public required WorkflowItemRef Item { get; init; }
    public required string StatusKey { get; init; }

    /// <summary>The activity (the waiting visit), once there is one: null while entering.</summary>
    public Guid? ActivityId { get; init; }

    public required IReadOnlyDictionary<string, JsonElement?> Values { get; init; }

    /// <summary>What the step kept when it began waiting.</summary>
    public JsonElement? State { get; init; }

    /// <summary>
    /// The item's values by name, as saved, as the account may read them (ADR 0086 step 0): a field it can't read, a
    /// restricted one included, is left out. Null when the account can't read the item, or where the host doesn't offer it.
    /// </summary>
    public virtual Task<IReadOnlyDictionary<string, JsonElement?>?> ValuesAsAsync(Guid accountId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyDictionary<string, JsonElement?>?>(null);

    /// <summary>Whether the account exists and is a service account (not an agent's); false where the host doesn't say.</summary>
    public virtual Task<bool> IsServiceAccountAsync(Guid accountId, CancellationToken cancellationToken) => Task.FromResult(false);

    /// <summary>Something only some hosts offer for this item (mailing a request's customers), or null where it has none.</summary>
    public virtual T? Feature<T>() where T : class => this as T;

    /// <summary>Adds a line to the step in the workflow tracer (ADR 0034).</summary>
    public virtual void Log(string message, string level = "info")
    {
    }
}

/// <summary>A visit waiting in a status of a plugin's type.</summary>
public sealed record PendingStep(Guid ActivityId, Guid ItemId, string StatusType, string StatusKey);

/// <summary>
/// Signals for waiting steps (ADR 0022), from outside the request that started them: a reply arrived, an outside system
/// answered. Toda itself signals a step when a customer comments on its record in public ({ kind: "reply", text,
/// author, via }).
/// </summary>
public interface IWorkflowSignals
{
    Task<IReadOnlyList<PendingStep>> PendingAsync(Guid itemId, CancellationToken cancellationToken = default);

    /// <summary>Resumes the step with the signal; false when it isn't waiting any more.</summary>
    Task<bool> SignalAsync(Guid activityId, JsonElement payload, CancellationToken cancellationToken = default);
}
