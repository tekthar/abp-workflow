using System.Text.Json;

namespace AbpWorkflow;

/// <summary>
/// The item a workflow moves, as the runner sees it inside the host's change: what it is, its status, its
/// values by name, who is acting and what they may do. The host opens one per change, in its own unit of work, and
/// writes what the runner asks for (a status, a field) the way it writes anything: checked, in its history, in the
/// change's change set. The runner never reads the host's entity. What only some hosts have (a record's children, its
/// project's lead, a request's customers) the session offers as features, through <see cref="Feature{T}"/>.
/// </summary>
public abstract class WorkflowItemSession
{
    public required WorkflowItemRef Item { get; init; }

    /// <summary>Who is acting: an account, or <see cref="Guid.Empty"/> for the system (the worker, an exit).</summary>
    public required Guid ActorId { get; init; }

    /// <summary>The context the runner keeps its own rows in (activities, decisions, traces): the host's, mapping the engine's tables.</summary>
    public required IWorkflowsDbContext Db { get; init; }

    /// <summary>The item's status now.</summary>
    public abstract string Status { get; }

    /// <summary>The item's values by name, as stored, now: after the change's own edits so far.</summary>
    public abstract Task<IReadOnlyDictionary<string, JsonElement?>> ValuesAsync(CancellationToken cancellationToken);

    /// <summary>The item's values by name as they were when the session opened, before the change's own edits.</summary>
    public abstract Task<IReadOnlyDictionary<string, JsonElement?>> BeforeAsync(CancellationToken cancellationToken);

    /// <summary>Moves the item to a status of its workflow, as part of the change, in its history.</summary>
    public abstract void SetStatus(WorkflowStatus target);

    /// <summary>An action's change to a field by its name; null clears it. Checked as the host checks any write, but not against the actor's field rights.</summary>
    public abstract Task SetFieldAsync(string field, JsonElement? value, CancellationToken cancellationToken);

    /// <summary>Whether the actor may change the item's status at all; a refusal says why.</summary>
    public abstract string? UnlessMayChangeStatus();

    public abstract Task<IReadOnlyList<Guid>> ActorGroupIdsAsync(CancellationToken cancellationToken);

    /// <summary>Whether the actor manages the item's scope (a project's admins).</summary>
    public abstract Task<bool> ActorManagesScopeAsync(CancellationToken cancellationToken);

    /// <summary>Whether the actor holds this account's assignments here now, as their delegate.</summary>
    public virtual Task<bool> ActorHoldsAssignmentsOfAsync(Guid accountId, CancellationToken cancellationToken) => Task.FromResult(false);

    /// <summary>The item's values by name as an account may read them; null when it can't read the item.</summary>
    public virtual Task<IReadOnlyDictionary<string, JsonElement?>?> ValuesAsAsync(Guid accountId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyDictionary<string, JsonElement?>?>(null);

    /// <summary>Whether the account exists and is a service account, not a person's or an agent's; false where the host doesn't say.</summary>
    public virtual Task<bool> IsServiceAccountAsync(Guid accountId, CancellationToken cancellationToken) => Task.FromResult(false);

    /// <summary>Something only some hosts offer for this item, or null where it has none.</summary>
    public virtual T? Feature<T>() where T : class => this as T;
}
