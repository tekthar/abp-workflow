using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Volo.Abp.DependencyInjection;

namespace Toda.Workflows;

/// <summary>
/// A host's items of one kind (ADR 0089), as the runner opens them outside a person's request: the worker moving an item
/// on from a waiting step, a signal resuming one. The host owns the item and its change; the runner only asks.
/// </summary>
public interface IWorkflowItemStore
{
    /// <summary>One of <see cref="Toda.Workflows.WorkflowItemKinds"/>.</summary>
    string Kind { get; }

    /// <summary>
    /// The item, opened as the system in the caller's unit of work (<paramref name="db"/>'s), with the workflow its type
    /// runs; null when it's gone. What the runner then asks for goes into the session's own change set, as the system.
    /// </summary>
    Task<(WorkflowItemSession Item, BoundWorkflow Bound)?> OpenAsSystemAsync(IWorkflowsDbContext db, Guid itemId, CancellationToken cancellationToken);

    /// <summary>Saves what the runner changed on an item opened as the system (its history, events, what depends on it), with the run's trace.</summary>
    Task SaveAsync(WorkflowItemSession item, WorkflowTrace? trace, CancellationToken cancellationToken);

    /// <summary>
    /// How many of its items that move by the workflow sit in each of these statuses, now: a published version can't drop
    /// a status items sit in. Statuses with none are left out.
    /// </summary>
    Task<IReadOnlyDictionary<string, int>> InStatusesAsync(IWorkflowsDbContext db, Guid workflowId, IReadOnlyCollection<string> statuses, CancellationToken cancellationToken);

    /// <summary>A new version of the workflow was published: what keeps a copy of its statuses (a record type's list, a schema's status rows) follows it.</summary>
    Task PublishedAsync(IWorkflowsDbContext db, Workflow workflow, CancellationToken cancellationToken);

    /// <summary>What one of its items is called in a sentence: "record", "asset".</summary>
    string Noun { get; }
}

/// <summary>
/// The stores there are, by kind: the host's own from DI, and a plugin's kind (ADR 0090) through the engine's bridge.
/// Read when needed, since the hosts' services take transitions through the runner themselves.
/// </summary>
public class WorkflowItemStores(IServiceProvider services, KindItems kinds) : ITransientDependency
{
    /// <summary>
    /// Every store there is: what publishing asks about the items a workflow moves. Plugins' kinds are those some
    /// workflow is bound for, their plugin on or not.
    /// </summary>
    public async Task<List<IWorkflowItemStore>> AllAsync(IWorkflowsDbContext db, CancellationToken cancellationToken)
    {
        List<IWorkflowItemStore> all = [.. services.GetServices<IWorkflowItemStore>()];
        foreach (string kind in await db.WorkflowBindings.AsNoTracking().Select(b => b.ItemKind).Distinct().ToListAsync(cancellationToken))
        {
            all.Add(new KindItemStore(kind, await kinds.NounAsync(kind), kinds));
        }

        return all;
    }

    public IWorkflowItemStore For(string kind) =>
        services.GetServices<IWorkflowItemStore>().FirstOrDefault(s => s.Kind == kind)
        ?? (WorkflowItemKindNames.IsPlugin(kind) ? new KindItemStore(kind, kind, kinds) : null)
        ?? throw new InvalidOperationException($"Nothing here moves '{kind}' items through workflows.");
}
