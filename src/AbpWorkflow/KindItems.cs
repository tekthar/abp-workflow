using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Volo.Abp;
using Volo.Abp.DependencyInjection;

namespace AbpWorkflow;

/// <summary>A plugin's item kind isn't there now (its plugin is off, or no longer has it): its items wait where they are until it's back.</summary>
public sealed class WorkflowItemKindOffException(string kind)
    : Exception($"'{kind}' isn't available: its plugin may be off. Its items stay where they are until it's back.")
{
    public string Kind { get; } = kind;
}

/// <summary>A plugin's item kind as the binding pages list it: strings only, so nothing of the plugin's is kept.</summary>
public sealed record WorkflowItemKindInfo(string Kind, string? Plugin, string Key, string Title, string Noun, string Scope, IReadOnlyList<WorkflowItemType> Types,
    IReadOnlyList<WorkflowItemField> Fields);

/// <summary>
/// The engine's bridge to items of plugins' own: it leases the plugin's <see cref="IWorkflowItemKind"/> for
/// each call and lets it go at once, inside the caller's unit of work, so the plugin's context joins the change and
/// nothing of the plugin's outlives the call. The item stays open on the host's side, in a <see cref="KindItemSession"/>.
/// </summary>
public class KindItems(IWorkflowExtensionSource source, IServiceProvider services) : ITransientDependency
{
    // Asked for when an item needs it, not when the engine's services are made: they're made on every change's path.
    internal IWorkflowPeople People => services.GetRequiredService<IWorkflowPeople>();

    /// <summary>Every kind the enabled plugins (and the host) offer now.</summary>
    public async Task<List<WorkflowItemKindInfo>> CatalogAsync()
    {
        await using IWorkflowLease<IWorkflowItemKind> lease = source.Lease<IWorkflowItemKind>(services);
        return [.. lease.Items.Where(i => WorkflowItemKindNames.IsKey(i.Instance.Key)).Select(i => new WorkflowItemKindInfo(
            Name(i.PluginKey, i.Instance), i.PluginKey, i.Instance.Key, i.Instance.Title, i.Instance.Noun, i.Instance.Scope,
            [.. i.Instance.Types.Select(t => new WorkflowItemType(t.Key, t.Title))], [.. i.Instance.Fields.Select(f => new WorkflowItemField(f.Name, f.Kind))]))];
    }

    /// <summary>The kind now, as the catalog says it; null when it isn't there.</summary>
    public async Task<WorkflowItemKindInfo?> FindAsync(string kind) => (await CatalogAsync()).FirstOrDefault(k => k.Kind == kind);

    /// <summary>
    /// An item of the kind, opened in the caller's change by <paramref name="actorId"/> (<see cref="Guid.Empty"/> for the
    /// system); null when it's gone. Throws <see cref="WorkflowItemKindOffException"/> when the kind isn't there.
    /// </summary>
    public Task<KindItemSession?> OpenAsync(IWorkflowsDbContext db, string kind, Guid id, Guid actorId, CancellationToken cancellationToken) =>
        WithAsync(kind, async found =>
        {
            if (await found.OpenAsync(id, cancellationToken) is not { } opened)
            {
                return null;
            }

            WorkflowItemSnapshot snapshot = Copy(opened, kind);
            if (snapshot.Item.Key.Length is 0 or > 64 || snapshot.Item.Type.Length is 0 or > 64)
            {
                throw new WorkflowFailureException($"A {found.Noun} needs a key and a type of at most 64 characters each to move by a workflow.");
            }

            WorkflowItemRights? rights = actorId == Guid.Empty ? null : Copy(await found.RightsAsync(snapshot, actorId, cancellationToken));
            return new KindItemSession(this, snapshot, rights, found.Noun)
            {
                Item = snapshot.Item,
                ActorId = actorId,
                Db = db,
            };
        });

    /// <summary>The workflow an item follows: its scope's binding, else the site's; null when its type is bound to none.</summary>
    public static async Task<BoundWorkflow?> BoundAsync(IWorkflowsDbContext db, string kind, string type, Guid? scopeId, CancellationToken cancellationToken)
    {
        Guid? workflowId = await db.WorkflowBindings.AsNoTracking()
            .Where(b => b.ItemKind == kind && b.ItemType == type && (b.ScopeId == scopeId || b.ScopeId == null))
            .OrderBy(b => b.ScopeId == null).Select(b => (Guid?)b.WorkflowId).FirstOrDefaultAsync(cancellationToken);
        return workflowId is { } id
            && await db.Workflows.AsNoTracking().Where(w => w.Id == id).Select(w => new { w.Version, w.Definition }).FirstOrDefaultAsync(cancellationToken) is { } w
            ? new BoundWorkflow(id, w.Version, w.Definition)
            : null;
    }

    /// <summary>
    /// Saves what the workflow changed on the item, in the caller's change: the run's trace, the item through its plugin
    /// (status, fields, version), then the engine's own rows. Nothing of it stays if any part fails.
    /// </summary>
    public async Task SaveAsync(KindItemSession item, WorkflowTrace? trace, CancellationToken cancellationToken)
    {
        if (trace is not null)
        {
            WorkflowTraces.Attach(item.Db, trace, item.Status, item.Changed ? item.ChangeSetId : null);
        }

        if (item.Changed)
        {
            WorkflowItemChange change = item.Change();
            await WithAsync<bool>(item.Item.Kind, async found =>
            {
                try
                {
                    await found.SaveAsync(change, cancellationToken);
                }
                catch (WorkflowItemException ex)
                {
                    throw new UserFriendlyException(ex.Message, ex.Conflict ? WorkflowErrorCodes.Conflict : null);
                }

                return true;
            });
        }

        await item.Db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>A field the workflow sets, as the item's plugin would store it; a refusal fails the run with its words.</summary>
    internal Task<JsonElement?> CheckFieldAsync(KindItemSession item, string field, JsonElement? value, CancellationToken cancellationToken) =>
        WithAsync(item.Item.Kind, async found =>
        {
            try
            {
                JsonElement? stored = await found.CheckFieldAsync(item.Snapshot, field, value?.Clone(), cancellationToken);
                return stored?.Clone();
            }
            catch (WorkflowItemException ex)
            {
                throw new WorkflowFailureException($"The workflow sets {field}: {ex.Message}");
            }
        });

    /// <summary>An account's rights to the item now (for reading its values as that account).</summary>
    internal Task<WorkflowItemRights> RightsAsync(KindItemSession item, Guid accountId, CancellationToken cancellationToken) =>
        WithAsync(item.Item.Kind, async found => Copy(await found.RightsAsync(item.Snapshot, accountId, cancellationToken)));

    /// <summary>
    /// The items a workflow moves for a kind, one selection per binding: a scope's own, and the site's, which leaves out
    /// the scopes that bind a workflow of their own for the type.
    /// </summary>
    public static async Task<List<WorkflowItemSelection>> SelectionsAsync(IWorkflowsDbContext db, string kind, Guid workflowId, CancellationToken cancellationToken)
    {
        List<WorkflowBinding> all = await db.WorkflowBindings.AsNoTracking().Where(b => b.ItemKind == kind).ToListAsync(cancellationToken);
        return [.. all.Where(b => b.WorkflowId == workflowId).Select(b => new WorkflowItemSelection(b.ItemType, b.ScopeId,
            b.ScopeId is null ? [.. all.Where(o => o.ItemType == b.ItemType && o.ScopeId is not null).Select(o => o.ScopeId!.Value)] : []))];
    }

    /// <summary>How many of the kind's items the workflow moves sit in each of these statuses (all, when null).</summary>
    public async Task<Dictionary<string, int>> InStatusesAsync(IWorkflowsDbContext db, string kind, Guid workflowId, IReadOnlyCollection<string>? statuses,
        CancellationToken cancellationToken)
    {
        List<WorkflowItemSelection> selections = await SelectionsAsync(db, kind, workflowId, cancellationToken);
        Dictionary<string, int> counts = [];
        if (selections.Count == 0)
        {
            return counts;
        }

        foreach (WorkflowItemSelection selection in selections)
        {
            foreach ((string status, int count) in await InStatusesAsync(kind, selection, statuses, cancellationToken))
            {
                counts[status] = counts.GetValueOrDefault(status) + count;
            }
        }

        return counts;
    }

    /// <summary>How many of the selected items sit in each of these statuses (all, when null).</summary>
    public Task<Dictionary<string, int>> InStatusesAsync(string kind, WorkflowItemSelection selection, IReadOnlyCollection<string>? statuses,
        CancellationToken cancellationToken) =>
        WithAsync(kind, async found => (await found.InStatusesAsync(selection, statuses is null ? null : [.. statuses], cancellationToken))
            .Where(c => c.Value > 0).ToDictionary(c => c.Key, c => c.Value));

    /// <summary>Binding's map: the selected items move from the statuses the new workflow lacks, in the change that binds it.</summary>
    public Task MoveStatusesAsync(string kind, WorkflowItemSelection selection, IReadOnlyDictionary<string, string> map, Guid changeSetId, Guid actorId,
        CancellationToken cancellationToken) =>
        WithAsync<bool>(kind, async found =>
        {
            await found.MoveStatusesAsync(selection, new Dictionary<string, string>(map), changeSetId, actorId, cancellationToken);
            return true;
        });

    /// <summary>A new version of a workflow the kind's items follow: each binding's items are told, while the kind is there.</summary>
    public async Task PublishedAsync(IWorkflowsDbContext db, string kind, Workflow workflow, CancellationToken cancellationToken)
    {
        List<WorkflowItemSelection> selections = await SelectionsAsync(db, kind, workflow.Id, cancellationToken);
        if (selections.Count == 0)
        {
            return;
        }

        List<WorkflowItemStatus> statuses = [.. workflow.Definition.Statuses.Select(s => new WorkflowItemStatus(s.Key, new Dictionary<string, string>(s.Names),
            JsonNamingPolicy.CamelCase.ConvertName(s.Category.ToString())))];
        await WithAsync<bool>(kind, async found =>
        {
            foreach (WorkflowItemSelection selection in selections)
            {
                await found.PublishedAsync(selection, workflow.Version, statuses, cancellationToken);
            }

            return true;
        });
    }

    /// <summary>What one of the kind's items is called, or its kind's key when it isn't there now.</summary>
    public async Task<string> NounAsync(string kind) =>
        (await FindAsync(kind))?.Noun ?? (WorkflowItemKindNames.IsPlugin(kind) ? kind[(kind.IndexOf(':', StringComparison.Ordinal) + 1)..] : kind);

    /// <summary>The kind for one use, inside the caller's change; the lease (and the plugin's scope) ends with it.</summary>
    private async Task<T> WithAsync<T>(string kind, Func<IWorkflowItemKind, Task<T>> work)
    {
        await using IWorkflowLease<IWorkflowItemKind> lease = source.Lease<IWorkflowItemKind>(services);
        IWorkflowItemKind found = lease.Items.Where(i => Name(i.PluginKey, i.Instance) == kind).Select(i => i.Instance).FirstOrDefault()
            ?? throw new WorkflowItemKindOffException(kind);
        return await work(found);
    }

    private static string Name(string? plugin, IWorkflowItemKind kind) => plugin is null ? kind.Key : WorkflowItemKindNames.Of(plugin, kind.Key);

    /// <summary>The plugin's snapshot copied into the host's own types: its collections and JSON documents never kept.</summary>
    private static WorkflowItemSnapshot Copy(WorkflowItemSnapshot item, string kind) =>
        new(item.Item with { Kind = kind }, item.Status, item.Version,
            item.Values.ToDictionary(v => v.Key, v => v.Value?.Clone(), StringComparer.Ordinal));

    private static WorkflowItemRights Copy(WorkflowItemRights rights) => new(rights.Read, rights.Move, rights.ManagesScope, rights.WhyNot);
}

/// <summary>
/// A plugin's item moving through a workflow, in the change that moves it: its values as its plugin read them,
/// with what the workflow set since, and the rights of who's acting. Saved through <see cref="KindItems.SaveAsync"/>.
/// </summary>
public sealed class KindItemSession : WorkflowItemSession
{
    private readonly KindItems _kinds;
    private readonly WorkflowItemRights? _rights;
    private readonly string _noun;
    private readonly Dictionary<string, JsonElement?> _fields = new(StringComparer.Ordinal);
    private string _status;

    internal KindItemSession(KindItems kinds, WorkflowItemSnapshot snapshot, WorkflowItemRights? rights, string noun)
    {
        _kinds = kinds;
        _rights = rights;
        _noun = noun;
        Snapshot = snapshot;
        _status = snapshot.Status;
    }

    /// <summary>The item as its plugin read it when the session opened.</summary>
    public WorkflowItemSnapshot Snapshot { get; }

    /// <summary>The change set every change of this session goes in.</summary>
    public Guid ChangeSetId { get; } = Guid.NewGuid();

    /// <summary>What one of these items is called: "test run".</summary>
    public string Noun => _noun;

    /// <summary>Whether the account acting may read the item; true for the system.</summary>
    public bool MayRead => _rights?.Read ?? true;

    /// <summary>Whether the account acting manages where the item lives; true for the system.</summary>
    public bool ManagesScope => _rights?.ManagesScope ?? true;

    public bool Changed => _status != Snapshot.Status || _fields.Count > 0;

    public override string Status => _status;

    public override Task<IReadOnlyDictionary<string, JsonElement?>> ValuesAsync(CancellationToken cancellationToken) => Task.FromResult(Values(true));

    public override Task<IReadOnlyDictionary<string, JsonElement?>> BeforeAsync(CancellationToken cancellationToken) => Task.FromResult(Values(false));

    private IReadOnlyDictionary<string, JsonElement?> Values(bool now)
    {
        Dictionary<string, JsonElement?> values = new(Snapshot.Values, StringComparer.Ordinal);
        if (now)
        {
            foreach ((string field, JsonElement? value) in _fields)
            {
                values[field] = value;
            }
        }

        values["status"] = JsonSerializer.SerializeToElement(now ? _status : Snapshot.Status);
        values["type"] = JsonSerializer.SerializeToElement(Item.Type);
        return values;
    }

    public override void SetStatus(WorkflowStatus target) => _status = target.Key;

    public override async Task SetFieldAsync(string field, JsonElement? value, CancellationToken cancellationToken)
    {
        if (field is "status" or "type")
        {
            throw new WorkflowFailureException($"The workflow sets {field}, which a workflow moves itself.");
        }

        _fields[field] = await _kinds.CheckFieldAsync(this, field, value, cancellationToken);
    }

    public override string? UnlessMayChangeStatus() =>
        _rights is null || _rights.Move ? null : _rights.WhyNot ?? $"You can't move this {_noun}.";

    public override Task<IReadOnlyList<Guid>> ActorGroupIdsAsync(CancellationToken cancellationToken) =>
        ActorId == Guid.Empty ? Task.FromResult<IReadOnlyList<Guid>>([]) : _kinds.People.GroupIdsAsync(ActorId, cancellationToken);

    public override Task<bool> ActorManagesScopeAsync(CancellationToken cancellationToken) => Task.FromResult(ManagesScope);

    public override async Task<IReadOnlyDictionary<string, JsonElement?>?> ValuesAsAsync(Guid accountId, CancellationToken cancellationToken) =>
        (await _kinds.RightsAsync(this, accountId, cancellationToken)).Read ? Values(true) : null;

    public override Task<bool> IsServiceAccountAsync(Guid accountId, CancellationToken cancellationToken) =>
        _kinds.People.IsServiceAccountAsync(accountId, cancellationToken);

    /// <summary>What to save: the status from and to, the fields set, the version it was read at.</summary>
    internal WorkflowItemChange Change() =>
        new(Item, Snapshot.Version, Snapshot.Status, _status, new Dictionary<string, JsonElement?>(_fields), ActorId == Guid.Empty ? null : ActorId, ChangeSetId);
}

/// <summary>A plugin's kind as the runner opens its items outside a person's request: the worker moving one on, a signal.</summary>
public sealed class KindItemStore(string kind, string noun, KindItems kinds) : IWorkflowItemStore
{
    public string Kind => kind;

    public string Noun => noun;

    public async Task<(WorkflowItemSession Item, BoundWorkflow Bound)?> OpenAsSystemAsync(IWorkflowsDbContext db, Guid itemId, CancellationToken cancellationToken)
    {
        if (await kinds.OpenAsync(db, kind, itemId, Guid.Empty, cancellationToken) is not { } item
            || await KindItems.BoundAsync(db, kind, item.Item.Type, item.Item.ScopeId, cancellationToken) is not { } bound)
        {
            return null;
        }

        return (item, bound);
    }

    public Task SaveAsync(WorkflowItemSession item, WorkflowTrace? trace, CancellationToken cancellationToken) =>
        kinds.SaveAsync((KindItemSession)item, trace, cancellationToken);

    public async Task<IReadOnlyDictionary<string, int>> InStatusesAsync(IWorkflowsDbContext db, Guid workflowId, IReadOnlyCollection<string> statuses,
        CancellationToken cancellationToken) =>
        await kinds.InStatusesAsync(db, kind, workflowId, statuses, cancellationToken);

    /// <summary>Told only while the kind is there; its items catch up the next time they move.</summary>
    public async Task PublishedAsync(IWorkflowsDbContext db, Workflow workflow, CancellationToken cancellationToken)
    {
        try
        {
            await kinds.PublishedAsync(db, kind, workflow, cancellationToken);
        }
        catch (WorkflowItemKindOffException)
        {
        }
    }
}
