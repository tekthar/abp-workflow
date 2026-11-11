using Microsoft.EntityFrameworkCore;
using Volo.Abp;
using Volo.Abp.DependencyInjection;

namespace AbpWorkflow;

/// <summary>The workflow an item's type runs: its id and published version (none for a plain status list) and the definition.</summary>
public sealed record BoundWorkflow(Guid? Id, int Version, WorkflowDefinition Definition);

/// <summary>
/// Who decides an approval: the person (<paramref name="Me"/>), deciding for themselves or for a delegator whose
/// delegation the host has checked, that one's groups read now, and whether the person manages the item's
/// scope (a project's or a schema's admins decide for the approvers).
/// </summary>
public sealed record ApprovalDecider(Guid Me, Guid Deciding, IReadOnlySet<Guid> Groups, bool ManagesScope);

/// <summary>A transition being taken, held by the host between the runner's phases.</summary>
public sealed class TransitionRun(TransitionContext context)
{
    public TransitionContext Context { get; } = context;
    public WorkflowTransition Transition => Context.Transition;
    public WorkflowTrace? Trace => Context.Trace;

    /// <summary>The status the transition leads to.</summary>
    public WorkflowStatus Target => Context.Workflow.Status(Context.Transition.To)!;
}

/// <summary>
/// Takes transitions: the pipeline over an item session. A
/// transition is a run in phases, and the host's own edits stay where they are between them:
/// <list type="number">
/// <item><see cref="Begin"/>: which transition the request means, the right to change the status, the trace;</item>
/// <item><see cref="CheckAsync"/>: its conditions, on the item as it was;</item>
/// <item>(the host applies the transition's fields and its own edits);</item>
/// <item><see cref="CompleteAsync"/>: checks on the item as it is now, then the status;</item>
/// <item>(the host's done-only work);</item>
/// <item><see cref="FinishAsync"/>: actions, then entering the target.</item>
/// </list>
/// The runner never commits: the host's change does, with everything the runner wrote.
/// </summary>
public class WorkflowRunner(WorkflowExtensions extensions, StatusEntry statusEntry, WorkflowsDb workflowsDb, WorkflowTraces traces, WorkflowItemStores stores,
    IWorkflowEvents events)
    : ITransientDependency
{
    /// <summary>
    /// The transition a request means (by key or number, or the one way to a status), checked for the right to change
    /// the status; null when the request moves nothing. Traced when the type runs a workflow of its own.
    /// </summary>
    public TransitionRun? Begin(WorkflowItemSession item, BoundWorkflow bound, string? transitionKey, string? status, string typeName)
    {
        WorkflowDefinition flow = bound.Definition;
        WorkflowTransition? transition = transitionKey is null && status is null
            ? null
            : WorkflowEngine.Resolve(flow, item.Status, transitionKey, status, typeName);
        if (transition is null)
        {
            return null;
        }

        if (item.UnlessMayChangeStatus() is { } refused)
        {
            throw new UserFriendlyException(refused, WorkflowErrorCodes.Forbidden);
        }

        WorkflowTrace? trace = bound.Id is { } workflowId
            ? new WorkflowTrace(item.Item.ScopeId ?? Guid.Empty, item.Item.Id, item.Item.Key, workflowId, bound.Version, WorkflowRunKinds.Transition, transition.Key,
                transition.Number, item.Status, transition.To, item.ActorId == Guid.Empty ? null : item.ActorId) { ItemKind = item.Item.Kind }
            : null;
        return new TransitionRun(new TransitionContext(item, flow, transition, item.Status)
        {
            WorkflowId = bound.Id,
            WorkflowVersion = bound.Version,
            Trace = trace,
        });
    }

    /// <summary>The transition's conditions, on the item as it was before the request's own fields; a refusal says why.</summary>
    public async Task CheckAsync(TransitionRun run, CancellationToken cancellationToken)
    {
        if (await extensions.ConditionsAsync(run.Transition.Conditions, run.Context, cancellationToken) is { } why)
        {
            throw new UserFriendlyException(why, WorkflowErrorCodes.Forbidden);
        }
    }

    /// <summary>Every check on the item as it is now (all their complaints at once), then the status, checked again against the item as it is.</summary>
    public async Task CompleteAsync(TransitionRun run, CancellationToken cancellationToken)
    {
        List<string> invalid = await extensions.CheckAsync(run.Transition.Checks, run.Context, cancellationToken);
        if (invalid.Count > 0)
        {
            throw new UserFriendlyException(string.Join(" ", invalid));
        }

        WorkflowItemSession item = run.Context.Item;
        if (item.Status == run.Target.Key)
        {
            return;
        }

        // The status's right is asked again: the request's own fields may have changed what a rule says.
        if (item.UnlessMayChangeStatus() is { } refused)
        {
            throw new UserFriendlyException(refused, WorkflowErrorCodes.Forbidden);
        }

        item.SetStatus(run.Target);
    }

    /// <summary>Actions, then the target's entry: sync types move on at once, async ones plan their work. A self loop enters nothing.</summary>
    public async Task FinishAsync(TransitionRun run, CancellationToken cancellationToken)
    {
        await extensions.RunAsync(run.Transition.Actions, run.Context, cancellationToken);
        if (run.Transition.To != run.Context.FromStatus)
        {
            await statusEntry.EnterAsync(run.Context, run.Target, 0, cancellationToken);
        }

        WorkflowItemSession item = run.Context.Item;
        await events.TransitionTakenAsync(new WorkflowTransitionTaken(item.Item, run.Transition.Key, run.Context.FromStatus, run.Transition.To, item.Status,
            item.ActorId == Guid.Empty ? null : item.ActorId), cancellationToken);
    }

    /// <summary>The transitions an actor may take from the item's status now: the open ones whose conditions hold.</summary>
    public async Task<List<WorkflowTransition>> OpenAsync(WorkflowItemSession item, WorkflowDefinition flow, CancellationToken cancellationToken)
    {
        List<WorkflowTransition> open = [];
        foreach (WorkflowTransition transition in WorkflowEngine.Available(flow, item.Status))
        {
            if (await extensions.ConditionsAsync(transition.Conditions, new TransitionContext(item, flow, transition, item.Status), cancellationToken) is null)
            {
                open.Add(transition);
            }
        }

        return open;
    }

    /// <summary>
    /// Moves an item on from an asynchronous status visit whose work came to an outcome, as the system (the
    /// third step), through its kind's store. Does nothing if the item already left that visit (the activity is then
    /// abandoned). False when there was nothing to do.
    /// </summary>
    public async Task<bool> AdvanceAsync(Guid activityId, CancellationToken cancellationToken = default)
    {
        WorkflowTrace? trace = null;
        return await traces.GuardAsync(() => trace, () => workflowsDb.RunAsync(async db =>
        {
            WorkflowActivity? activity = await db.WorkflowActivities.FirstOrDefaultAsync(a => a.Id == activityId, cancellationToken);
            if (activity is null || activity.State != WorkflowActivityStates.Completed || activity.Outcome is null)
            {
                return false;
            }

            IWorkflowItemStore store = stores.For(activity.ItemKind);
            (WorkflowItemSession Item, BoundWorkflow Bound)? opened;
            try
            {
                opened = await store.OpenAsSystemAsync(db, activity.ItemId, cancellationToken);
            }
            catch (WorkflowItemKindOffException ex)
            {
                // A plugin's item whose plugin is off: it stays in this visit, and goes on once the plugin is back.
                activity.LastError = ex.Message;
                activity.NextSweepAt = DateTime.UtcNow + WorkflowSignals.PluginOffRetry;
                await db.SaveChangesAsync(cancellationToken);
                return false;
            }

            (WorkflowItemSession item, BoundWorkflow bound) = opened ?? default;
            bool latest = !await db.WorkflowActivities.AnyAsync(a => a.ItemId == activity.ItemId && a.EnteredAt > activity.EnteredAt, cancellationToken);
            if (item is null || item.Status != activity.StatusKey || !latest)
            {
                activity.State = WorkflowActivityStates.Abandoned;
                await db.SaveChangesAsync(cancellationToken);
                return false;
            }

            WorkflowDefinition started = await db.WorkflowVersions.AsNoTracking()
                .Where(v => v.WorkflowId == activity.WorkflowId && v.Version == activity.WorkflowVersion).Select(v => v.Definition)
                .FirstOrDefaultAsync(cancellationToken) ?? bound.Definition;
            WorkflowStatus from = started.Status(activity.StatusKey) ?? bound.Definition.Status(activity.StatusKey)
                ?? throw new UserFriendlyException($"The workflow no longer has status '{activity.StatusKey}'.");
            string? leadsTo = (from.Exits ?? []).GetValueOrDefault(activity.Outcome);
            trace = new WorkflowTrace(item.Item.ScopeId ?? Guid.Empty, item.Item.Id, item.Item.Key, activity.WorkflowId, activity.WorkflowVersion, WorkflowRunKinds.Exit,
                activity.Outcome, 0, item.Status, leadsTo ?? item.Status, null) { ItemKind = item.Item.Kind };
            TransitionContext context = new(item, bound.Definition, WorkflowEngine.Implicit(from), item.Status)
            {
                WorkflowId = bound.Id,
                WorkflowVersion = bound.Version,
                Trace = trace,
            };
            await statusEntry.LeaveAsync(context, from, activity.Outcome, 0, cancellationToken);
            activity.State = WorkflowActivityStates.Advanced;
            activity.AdvancedAt = DateTime.UtcNow;
            await events.ItemAdvancedAsync(new WorkflowItemAdvanced(item.Item, from.Key, activity.Outcome, item.Status), cancellationToken);
            await store.SaveAsync(item, trace, cancellationToken);
            return true;
        }, requiresNew: true, cancellationToken, isTransactional: true));
    }

    /// <summary>
    /// Records a decision on an item's pending approval: its approvers (named, from a field, or members of an
    /// approver group, read now) and the scope's admins (marked as such). One person, one decision: as themselves or for
    /// one delegator, never both. One rejection rejects; enough approvals approve, and the visit is then completed for
    /// the worker, or the host, to move the item on. Saved in the caller's change.
    /// </summary>
    public async Task<WorkflowActivity> DecideAsync(IWorkflowsDbContext db, WorkflowItemRef item, string itemStatus, Guid approvalId, ApprovalDecider decider,
        bool approve, string? comment, CancellationToken cancellationToken)
    {
        WorkflowActivity activity = await db.WorkflowActivities.FirstOrDefaultAsync(a => a.Id == approvalId && a.ItemId == item.Id
                && a.StatusType == WorkflowCatalog.Approval, cancellationToken)
            ?? throw new Volo.Abp.Domain.Entities.EntityNotFoundException(typeof(WorkflowActivity), approvalId);
        if (activity.State != WorkflowActivityStates.Pending || itemStatus != activity.StatusKey)
        {
            throw new UserFriendlyException($"This approval of {item.Key} is over ({activity.State}).", WorkflowErrorCodes.Conflict);
        }

        bool approver = activity.ApproverAccountIds.Contains(decider.Deciding) || activity.ApproverGroupIds.Any(decider.Groups.Contains);
        bool forSomeone = decider.Deciding != decider.Me;
        if (forSomeone)
        {
            if (!approver)
            {
                throw new UserFriendlyException($"They aren't one of the approvers of {item.Key}.", WorkflowErrorCodes.Forbidden);
            }
        }
        else if (!approver && !decider.ManagesScope)
        {
            throw new UserFriendlyException($"You aren't one of the approvers of {item.Key}.", WorkflowErrorCodes.Forbidden);
        }

        // One person, one decision: the delegate decides once, as themselves or for one delegator; the delegator once.
        if (await db.ApprovalDecisions.AnyAsync(d => d.ActivityId == activity.Id && (d.AccountId == decider.Me || d.ActingAccountId == decider.Me), cancellationToken))
        {
            throw new UserFriendlyException("You've already decided on this approval.", WorkflowErrorCodes.Conflict);
        }

        if (forSomeone && await db.ApprovalDecisions.AnyAsync(d => d.ActivityId == activity.Id && d.AccountId == decider.Deciding, cancellationToken))
        {
            throw new UserFriendlyException("They've already decided on this approval.", WorkflowErrorCodes.Conflict);
        }

        ApprovalDecision decision = new(Guid.NewGuid(), activity.Id, item.Id, decider.Deciding, approve,
            string.IsNullOrWhiteSpace(comment) ? null : comment.Trim(), asAdmin: !approver);
        if (forSomeone)
        {
            (decision.ActingAccountId, decision.ActingMode) = (decider.Me, WorkflowActingModes.Delegation);
        }

        db.ApprovalDecisions.Add(decision);
        int approvals = await db.ApprovalDecisions.CountAsync(d => d.ActivityId == activity.Id && d.Approved, cancellationToken) + (approve ? 1 : 0);
        if (!approve || approvals >= activity.Required)
        {
            activity.Outcome = approve ? "approved" : "rejected";
            activity.State = WorkflowActivityStates.Completed;
            activity.CompletedAt = DateTime.UtcNow;
            activity.NextSweepAt = DateTime.UtcNow.AddSeconds(30); // the sweeper's fallback if advancing fails
        }

        await events.ApprovalDecidedAsync(new WorkflowApprovalDecided(item, activity.Id, activity.StatusKey, decider.Deciding, approve,
            activity.State == WorkflowActivityStates.Completed ? activity.Outcome : null), cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return activity;
    }

    /// <summary>Whether a new item's first status does something on entry (an AI step, a wait, an approval, an automated or a decision status).</summary>
    public bool StartsWithWork(WorkflowDefinition flow, string status) => flow.Status(status) is { } first && statusEntry.Works(first);

    /// <summary>
    /// A new item's first status, entered as a move into it is when it does something, so an incoming item is triaged,
    /// timed or routed from the start (M7 step 17). Only for a status <see cref="StartsWithWork"/> says works.
    /// </summary>
    public async Task EnterFirstAsync(WorkflowItemSession item, BoundWorkflow bound, CancellationToken cancellationToken)
    {
        WorkflowStatus first = bound.Definition.Status(item.Status)!;
        WorkflowTransition create = new(0, "create", new Dictionary<string, string> { ["en"] = "Create" }, [], first.Key);
        // The activity names the workflow version it started in, which the worker reads its status from.
        await statusEntry.EnterAsync(new TransitionContext(item, bound.Definition, create, first.Key) { WorkflowId = bound.Id, WorkflowVersion = bound.Version },
            first, 0, cancellationToken);
    }
}
