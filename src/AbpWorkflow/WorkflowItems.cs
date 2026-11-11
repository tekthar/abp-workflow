using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Volo.Abp;
using Volo.Abp.DependencyInjection;
using Volo.Abp.Domain.Entities;

namespace AbpWorkflow;

/// <summary>
/// An item's place in its workflow: its status (and that status as the workflow has it, when it does), its version, the
/// workflow it follows, the step it waits in, and, when its kind isn't there now (a plugin that's off), why.
/// </summary>
public sealed record WorkflowItemState(string Kind, Guid Id, string? Key, string? Status, WorkflowStatus? Known, int Version, Guid? WorkflowId, string? Workflow,
    WorkflowItemWaiting? Waiting, string? Unavailable);

/// <summary>The step an item waits in: the visit's id, its status, the status's type, the visit's state, and the type's panel to show with it, if any.</summary>
public sealed record WorkflowItemWaiting(Guid Id, string StatusKey, string Type, string State, DateTime EnteredAt, DateTime? DueAt, string? Panel);

/// <summary>A transition an actor may take now, and the status it leads to.</summary>
public sealed record WorkflowOpenTransition(WorkflowTransition Transition, WorkflowStatus To);

/// <summary>One of an item's approvals with its decisions so far, and whether the actor may decide it now.</summary>
public sealed record WorkflowItemApproval(WorkflowActivity Activity, IReadOnlyList<ApprovalDecision> Decisions, bool MayDecide);

/// <summary>A kind's type as an admin binds it: the workflow (by key) it follows here, and whether that's the site's, inherited in a scope.</summary>
public sealed record WorkflowItemTypeBinding(string Key, string Title, string? Workflow, bool Inherited);

/// <summary>A kind of item and its bindings; <c>Enabled</c> false when the kind isn't there now and only its bindings are left.</summary>
public sealed record WorkflowItemKindBindings(string Kind, string? Plugin, string Key, string Title, string Noun, string Scope, bool Enabled,
    IReadOnlyList<WorkflowItemTypeBinding> Types);

/// <summary>Items per status the new workflow lacks, and whether the binding was made.</summary>
public sealed record WorkflowBindResult(Dictionary<string, int> Stranded, bool Applied);

/// <summary>
/// Items of any kind moving through workflows: the kinds an <see cref="IWorkflowItemKind"/> describes,
/// a host's own or a plugin's. A new item's first status, its place, the transitions open to an actor, taking one,
/// approvals and decisions, its runs, and binding a kind's type to a workflow, each in a change of its own. The kind
/// answers for its items (reading, rights, saving); this is the rest. A host puts its own checks (who's signed in,
/// read-only modes) and its own shapes for the API around it.
/// </summary>
public class WorkflowItems(
    WorkflowsDb workflowsDb, KindItems kinds, WorkflowRunner runner, WorkflowTraces traces, WorkflowExtensions extensions, StatusEntry statusEntry,
    IWorkflowPeople people) : ITransientDependency
{
    /// <summary>The workflow a kind's type follows in a scope (its own binding, else the site's); null when it's bound to none.</summary>
    public Task<BoundWorkflow?> BoundAsync(string kind, string type, Guid? scopeId, CancellationToken cancellationToken = default) =>
        workflowsDb.RunAsync(db => KindItems.BoundAsync(db, kind, type, scopeId, cancellationToken), cancellationToken: cancellationToken);

    /// <summary>
    /// A new item enters its first status, as the actor, in a change of its own: an approval asks, a wait starts. Call it
    /// once the item is saved, in the status <see cref="BoundAsync"/>'s definition starts in.
    /// </summary>
    public async Task<WorkflowItemState> StartAsync(Guid actorId, string kind, Guid id, CancellationToken cancellationToken = default)
    {
        await Moving(() => workflowsDb.RunAsync(async db =>
        {
            KindItemSession item = await OpenAsync(db, kind, id, actorId, cancellationToken);
            if (await KindItems.BoundAsync(db, kind, item.Item.Type, item.Item.ScopeId, cancellationToken) is { } bound
                && runner.StartsWithWork(bound.Definition, item.Status))
            {
                await runner.EnterFirstAsync(item, bound, cancellationToken);
                await kinds.SaveAsync(item, null, cancellationToken);
            }

            return true;
        }, requiresNew: true, cancellationToken, isTransactional: true));
        return await GetAsync(actorId, kind, id, cancellationToken) ?? throw new EntityNotFoundException(typeof(WorkflowItemSnapshot), id);
    }

    /// <summary>The item's place in its workflow as the actor may see it; null when they can't read it. While its kind is off, what's known of it, and why.</summary>
    public Task<WorkflowItemState?> GetAsync(Guid actorId, string kind, Guid id, CancellationToken cancellationToken = default) =>
        workflowsDb.RunAsync<WorkflowItemState?>(async db =>
        {
            KindItemSession? item;
            try
            {
                item = await kinds.OpenAsync(db, kind, id, actorId, cancellationToken);
            }
            catch (WorkflowItemKindOffException ex)
            {
                return new WorkflowItemState(kind, id, null, null, null, 0, null, null, await WaitingAsync(db, kind, id, null, cancellationToken), ex.Message);
            }

            if (item is null || !item.MayRead)
            {
                return null;
            }

            BoundWorkflow? bound = await KindItems.BoundAsync(db, kind, item.Item.Type, item.Item.ScopeId, cancellationToken);
            string? workflow = bound?.Id is { } workflowId
                ? await db.Workflows.AsNoTracking().Where(w => w.Id == workflowId).Select(w => w.Key).FirstOrDefaultAsync(cancellationToken)
                : null;
            return new WorkflowItemState(kind, id, item.Item.Key, item.Status.Length > 0 ? item.Status : null, bound?.Definition.Status(item.Status),
                item.Snapshot.Version, bound?.Id, workflow, await WaitingAsync(db, kind, id, item.Status, cancellationToken), null);
        }, cancellationToken: cancellationToken);

    /// <summary>The transitions the actor may take from the item's status now; empty when it follows no workflow or they can't move it, null when they can't read it.</summary>
    public Task<IReadOnlyList<WorkflowOpenTransition>?> TransitionsAsync(Guid actorId, string kind, Guid id, CancellationToken cancellationToken = default) =>
        Moving(() => workflowsDb.RunAsync<IReadOnlyList<WorkflowOpenTransition>?>(async db =>
        {
            if (await kinds.OpenAsync(db, kind, id, actorId, cancellationToken) is not { MayRead: true } item)
            {
                return null;
            }

            if (await KindItems.BoundAsync(db, kind, item.Item.Type, item.Item.ScopeId, cancellationToken) is not { } bound || item.UnlessMayChangeStatus() is not null)
            {
                return [];
            }

            return [.. (await runner.OpenAsync(item, bound.Definition, cancellationToken)).Select(t => new WorkflowOpenTransition(t, bound.Definition.Status(t.To)!))];
        }, cancellationToken: cancellationToken));

    /// <summary>
    /// Takes a transition (by key or number), as the actor, in one change: conditions, the transition's screen fields,
    /// checks, the status, actions and the target's entry. <paramref name="version"/>, when given, must be the
    /// item's: someone else's change in between is a conflict. Refused or failed, the run is still traced.
    /// </summary>
    public async Task<WorkflowItemState> TakeAsync(Guid actorId, string kind, Guid id, string transition, int? version = null,
        IReadOnlyDictionary<string, JsonElement?>? fields = null, CancellationToken cancellationToken = default)
    {
        WorkflowTrace? trace = null; // outside the change, so a refusal is still written
        await Moving(() => traces.GuardAsync(() => trace, () => workflowsDb.RunAsync(async db =>
        {
            KindItemSession item = await OpenAsync(db, kind, id, actorId, cancellationToken);
            if (version is { } expected && expected != item.Snapshot.Version)
            {
                throw new UserFriendlyException($"{item.Item.Key} was changed by someone else since you opened it. Open it again to see their change.",
                    WorkflowErrorCodes.Conflict);
            }

            BoundWorkflow bound = await KindItems.BoundAsync(db, kind, item.Item.Type, item.Item.ScopeId, cancellationToken)
                ?? throw new UserFriendlyException($"This {item.Noun} follows no workflow: its type isn't bound to one here.");
            TransitionRun run = runner.Begin(item, bound, transition, null, item.Noun)!;
            trace = run.Trace;
            await runner.CheckAsync(run, cancellationToken);
            foreach ((string field, JsonElement? value) in fields ?? new Dictionary<string, JsonElement?>())
            {
                if (!run.Transition.Fields.Contains(field))
                {
                    throw new UserFriendlyException($"'{transition}' doesn't ask for {field}.");
                }

                await item.SetFieldAsync(field, value, cancellationToken);
            }

            await runner.CompleteAsync(run, cancellationToken);
            await runner.FinishAsync(run, cancellationToken);
            await kinds.SaveAsync(item, trace, cancellationToken);
            return true;
        }, requiresNew: true, cancellationToken, isTransactional: true)));
        return await GetAsync(actorId, kind, id, cancellationToken) ?? throw new EntityNotFoundException(typeof(WorkflowItemSnapshot), id);
    }

    /// <summary>The item's approvals, newest first, and whether the actor may decide each now; null when they can't read it.</summary>
    public async Task<IReadOnlyList<WorkflowItemApproval>?> ApprovalsAsync(Guid actorId, string kind, Guid id, CancellationToken cancellationToken = default)
    {
        var found = await Moving(() => workflowsDb.RunAsync(async db =>
        {
            if (await kinds.OpenAsync(db, kind, id, actorId, cancellationToken) is not { MayRead: true } item)
            {
                return null;
            }

            List<WorkflowActivity> activities = await db.WorkflowActivities.AsNoTracking()
                .Where(a => a.ItemId == id && a.ItemKind == kind && a.StatusType == WorkflowCatalog.Approval).OrderByDescending(a => a.EnteredAt).ToListAsync(cancellationToken);
            List<Guid> ids = [.. activities.Select(a => a.Id)];
            return new
            {
                item.Status, item.ManagesScope, Moves = item.UnlessMayChangeStatus() is null, activities,
                decisions = await db.ApprovalDecisions.AsNoTracking().Where(d => ids.Contains(d.ActivityId)).OrderBy(d => d.At).ToListAsync(cancellationToken),
            };
        }, cancellationToken: cancellationToken));
        if (found is null)
        {
            return null;
        }

        HashSet<Guid> myGroups = [.. await people.GroupIdsAsync(actorId, cancellationToken)];
        bool admin = found.ManagesScope && found.Moves;
        return [.. found.activities.Select(a =>
        {
            List<ApprovalDecision> theirs = [.. found.decisions.Where(d => d.ActivityId == a.Id)];
            bool open = a.State == WorkflowActivityStates.Pending && found.Status == a.StatusKey;
            bool decided = theirs.Any(d => d.AccountId == actorId || d.ActingAccountId == actorId);
            bool eligible = a.ApproverAccountIds.Contains(actorId) || a.ApproverGroupIds.Any(myGroups.Contains) || admin;
            return new WorkflowItemApproval(a, theirs, open && eligible && !decided);
        })];
    }

    /// <summary>The actor's decision on the item's pending approval; once it's decided, the item moves on through the outcome's exit.</summary>
    public async Task<WorkflowItemState> DecideAsync(Guid actorId, string kind, Guid id, Guid approvalId, bool approve, string? comment = null,
        CancellationToken cancellationToken = default)
    {
        HashSet<Guid> myGroups = [.. await people.GroupIdsAsync(actorId, cancellationToken)];
        (Guid activityId, bool decided) = await Moving(() => workflowsDb.RunAsync(async db =>
        {
            KindItemSession item = await OpenAsync(db, kind, id, actorId, cancellationToken);
            WorkflowActivity activity = await runner.DecideAsync(db, item.Item, item.Status, approvalId,
                new ApprovalDecider(actorId, actorId, myGroups, item.ManagesScope && item.UnlessMayChangeStatus() is null), approve, comment, cancellationToken);
            return (activity.Id, activity.State == WorkflowActivityStates.Completed);
        }, requiresNew: true, cancellationToken, isTransactional: true));
        if (decided)
        {
            await Moving(() => runner.AdvanceAsync(activityId, cancellationToken));
        }

        return await GetAsync(actorId, kind, id, cancellationToken) ?? throw new EntityNotFoundException(typeof(WorkflowItemSnapshot), id);
    }

    /// <summary>The item's runs, newest first (at most 200): its history (moved only), or every run, refused ones too; null when the actor can't read it.</summary>
    public Task<List<WorkflowRun>?> RunsAsync(Guid actorId, string kind, Guid id, bool movedOnly = false, CancellationToken cancellationToken = default) =>
        Moving(() => workflowsDb.RunAsync<List<WorkflowRun>?>(async db =>
        {
            if (await kinds.OpenAsync(db, kind, id, actorId, cancellationToken) is not { MayRead: true })
            {
                return null;
            }

            IQueryable<WorkflowRun> query = db.WorkflowRuns.AsNoTracking().Where(r => r.ItemId == id && r.ItemKind == kind);
            if (movedOnly)
            {
                query = query.Where(r => r.Outcome == WorkflowRunOutcomes.Moved);
            }

            return await query.OrderByDescending(r => r.StartedAt).Take(200).ToListAsync(cancellationToken);
        }, cancellationToken: cancellationToken));

    /// <summary>The kinds there are and their bindings: at the site (<paramref name="scopeId"/> null), or in a scope, with what it inherits from the site.</summary>
    public Task<List<WorkflowItemKindBindings>> KindsAsync(Guid? scopeId, CancellationToken cancellationToken = default) =>
        workflowsDb.RunAsync(async db =>
        {
            List<WorkflowItemKindInfo> catalog = await kinds.CatalogAsync();
            // The site's page sees every binding, so a kind that's off shows wherever it's bound.
            List<WorkflowBinding> bindings = await db.WorkflowBindings.AsNoTracking().Where(b => scopeId == null || b.ScopeId == null || b.ScopeId == scopeId)
                .ToListAsync(cancellationToken);
            Dictionary<Guid, string> keys = await db.Workflows.AsNoTracking().Where(w => bindings.Select(b => b.WorkflowId).Contains(w.Id))
                .ToDictionaryAsync(w => w.Id, w => w.Key, cancellationToken);
            List<WorkflowItemKindBindings> listed = [];
            foreach (WorkflowItemKindInfo info in catalog.Where(k => scopeId is null || k.Scope == WorkflowItemScopes.Project))
            {
                listed.Add(new WorkflowItemKindBindings(info.Kind, info.Plugin, info.Key, info.Title, info.Noun, info.Scope, true,
                    [.. info.Types.Select(t => TypeBinding(info.Kind, t.Key, t.Title, bindings, keys, scopeId))]));
            }

            // Kinds that are off, by what's bound for them: their items wait where they are.
            foreach (IGrouping<string, WorkflowBinding> off in bindings.Where(b => catalog.All(k => k.Kind != b.ItemKind)).GroupBy(b => b.ItemKind))
            {
                string key = off.Key[(off.Key.IndexOf(':', StringComparison.Ordinal) + 1)..];
                listed.Add(new WorkflowItemKindBindings(off.Key, WorkflowItemKindNames.IsPlugin(off.Key) ? WorkflowItemKindNames.Plugin(off.Key) : null, key, key, key,
                    WorkflowItemScopes.Project, false, [.. off.Select(b => b.ItemType).Distinct().Select(t => TypeBinding(off.Key, t, t, bindings, keys, scopeId))]));
            }

            return listed;
        }, cancellationToken: cancellationToken);

    /// <summary>
    /// Binds a kind's type to a workflow (by key or id) at the site (<paramref name="scopeId"/> null: a site workflow) or
    /// in a scope (its own or a site workflow), or unbinds it (<paramref name="workflow"/> null). Items in statuses the
    /// workflow lacks are moved by <paramref name="map"/> (old key to new); with <paramref name="apply"/> false nothing
    /// changes and the result says what would be stranded. Who may bind is the caller's to check.
    /// </summary>
    public async Task<WorkflowBindResult> BindAsync(Guid actorId, string kind, string type, Guid? scopeId, string? workflow,
        IReadOnlyDictionary<string, string>? map = null, bool apply = true, Guid? changeSetId = null, CancellationToken cancellationToken = default)
    {
        WorkflowItemKindInfo info = await kinds.FindAsync(kind)
            ?? throw new UserFriendlyException($"'{kind}' isn't available now: its items can't be bound until it is.", WorkflowErrorCodes.Conflict);
        if (info.Types.All(t => t.Key != type))
        {
            throw new EntityNotFoundException(typeof(WorkflowItemType), type);
        }

        if (scopeId is not null && info.Scope == WorkflowItemScopes.Site)
        {
            throw new UserFriendlyException($"{info.Title} are the site's: their workflow is bound at the site.");
        }

        return await workflowsDb.RunAsync(async db =>
        {
            WorkflowBinding? binding = await db.WorkflowBindings.FirstOrDefaultAsync(b => b.ItemKind == kind && b.ItemType == type && b.ScopeId == scopeId, cancellationToken);
            if (workflow is null)
            {
                if (apply && binding is not null)
                {
                    db.WorkflowBindings.Remove(binding);
                    await db.SaveChangesAsync(cancellationToken);
                }

                return new WorkflowBindResult([], apply);
            }

            Workflow chosen = await db.Workflows.FirstOrDefaultAsync(w => (w.ScopeId == null || (scopeId != null && w.ScopeId == scopeId))
                    && (w.Key == workflow || w.Id.ToString() == workflow), cancellationToken)
                ?? throw new UserFriendlyException(scopeId is null
                    ? $"There's no site workflow '{workflow}'."
                    : $"There's no workflow '{workflow}' here: the scope's own or a site workflow.");
            if (chosen.GeneratedForTypeId is not null)
            {
                throw new UserFriendlyException($"{chosen.Name()} belongs to a record type: bind a workflow of its own.");
            }

            List<string> problems = await extensions.NotForKindAsync(chosen.Definition, kind, cancellationToken);
            if (problems.Count > 0)
            {
                throw new UserFriendlyException($"{chosen.Name()} can't move {info.Title.ToLowerInvariant()}: {string.Join(" ", problems)}");
            }

            WorkflowItemSelection selection = new(type, scopeId, scopeId is null
                ? await db.WorkflowBindings.Where(b => b.ItemKind == kind && b.ItemType == type && b.ScopeId != null).Select(b => b.ScopeId!.Value).ToListAsync(cancellationToken)
                : []);
            Dictionary<string, int> stranded = (await kinds.InStatusesAsync(kind, selection, null, cancellationToken))
                .Where(c => chosen.Definition.Status(c.Key) is null).ToDictionary(c => c.Key, c => c.Value);
            if (!apply)
            {
                return new WorkflowBindResult(stranded, false);
            }

            IReadOnlyDictionary<string, string> moves = map ?? new Dictionary<string, string>();
            if (stranded.Keys.FirstOrDefault(k => !moves.TryGetValue(k, out string? to) || chosen.Definition.Status(to) is null) is { } unmapped)
            {
                throw new UserFriendlyException($"Map each status the workflow lacks to one of its statuses: '{unmapped}' has {stranded[unmapped]} {info.Noun}(s).");
            }

            if (stranded.Count > 0)
            {
                await kinds.MoveStatusesAsync(kind, selection, stranded.Keys.ToDictionary(k => k, k => moves[k]), changeSetId ?? Guid.NewGuid(), actorId, cancellationToken);
            }

            if (binding is null)
            {
                db.WorkflowBindings.Add(new WorkflowBinding(Guid.NewGuid(), kind, type, scopeId, chosen.Id));
            }
            else
            {
                binding.WorkflowId = chosen.Id;
            }

            await db.SaveChangesAsync(cancellationToken);
            return new WorkflowBindResult(stranded, true);
        }, requiresNew: true, cancellationToken, isTransactional: true);
    }

    private static WorkflowItemTypeBinding TypeBinding(string kind, string type, string title, List<WorkflowBinding> bindings, Dictionary<Guid, string> keys, Guid? scopeId)
    {
        WorkflowBinding? own = bindings.FirstOrDefault(b => b.ItemKind == kind && b.ItemType == type && b.ScopeId == scopeId);
        WorkflowBinding? site = scopeId is null ? null : bindings.FirstOrDefault(b => b.ItemKind == kind && b.ItemType == type && b.ScopeId == null);
        WorkflowBinding? used = own ?? site;
        return new WorkflowItemTypeBinding(type, title, used is null ? null : keys.GetValueOrDefault(used.WorkflowId), own is null && site is not null);
    }

    /// <summary>The item opened by the actor in the change; not found when it's gone or they can't read it.</summary>
    private async Task<KindItemSession> OpenAsync(IWorkflowsDbContext db, string kind, Guid id, Guid actorId, CancellationToken cancellationToken) =>
        await kinds.OpenAsync(db, kind, id, actorId, cancellationToken) is { MayRead: true } item
            ? item
            : throw new EntityNotFoundException(typeof(WorkflowItemSnapshot), id);

    /// <summary>The step the item waits in: the latest visit of its status (any, when that isn't known) that isn't over.</summary>
    private async Task<WorkflowItemWaiting?> WaitingAsync(IWorkflowsDbContext db, string kind, Guid id, string? status, CancellationToken cancellationToken)
    {
        WorkflowActivity? waiting = await db.WorkflowActivities.AsNoTracking()
            .Where(a => a.ItemId == id && a.ItemKind == kind && (status == null || a.StatusKey == status) && (a.State == WorkflowActivityStates.Pending
                || a.State == WorkflowActivityStates.Completed || a.State == WorkflowActivityStates.TimedOut || a.State == WorkflowActivityStates.Failed))
            .OrderByDescending(a => a.EnteredAt)
            .FirstOrDefaultAsync(cancellationToken);
        return waiting is null
            ? null
            : new WorkflowItemWaiting(waiting.Id, waiting.StatusKey, waiting.StatusType, waiting.State, waiting.EnteredAt, waiting.DueAt,
                await statusEntry.PanelAsync(waiting.StatusType, waiting.State));
    }

    /// <summary>A kind that's off can't move its items: a conflict that says so.</summary>
    private static async Task<T> Moving<T>(Func<Task<T>> work)
    {
        try
        {
            return await work();
        }
        catch (WorkflowItemKindOffException ex)
        {
            throw new UserFriendlyException(ex.Message, WorkflowErrorCodes.Conflict);
        }
    }
}
