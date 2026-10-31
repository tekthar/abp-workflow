using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Volo.Abp.DependencyInjection;

namespace Toda.Workflows;

/// <summary>
/// Runs plugins' status types (ADR 0022): looks the type up for each call (never keeps a plugin's type, ADR 0003) and
/// gives it the record as a <see cref="WorkflowStatusContext"/>. Null when the type's plugin isn't there: the caller
/// decides what that means (a record can't enter; a waiting one stays and says so).
/// </summary>
public class PluginStatusTypes(IWorkflowExtensionSource source, IServiceProvider services, WorkflowItemStores stores, ILogger<PluginStatusTypes> logger)
    : ITransientDependency
{
    /// <summary>The record has just come into a status of a plugin's type, inside the change that brought it.</summary>
    public async Task<StatusEntryResult?> EnterAsync(TransitionContext context, WorkflowStatus status, CancellationToken cancellationToken)
    {
        await using IWorkflowLease<IWorkflowStatusType> lease = source.Lease<IWorkflowStatusType>(services);
        if (Find(lease, status.Type) is not { } type)
        {
            return null;
        }

        StepContext step = await StepAsync(context.Item, status.Key, null, null, context.Trace, cancellationToken);
        StatusEntryResult result = await type.EnterAsync(step, status.Settings, cancellationToken);
        if (result.Exit is { } exit && !Allowed(type, status).Contains(exit))
        {
            throw new WorkflowFailureException($"Status '{status.Key}' ({status.Type}) left by '{exit}', which isn't one of its exits.");
        }

        return result;
    }

    /// <summary>
    /// A waiting step's time came (<paramref name="signal"/> null) or a signal arrived: its type's answer. The record is
    /// tracked in <paramref name="db"/>: a reply code the step makes is saved with the caller's change.
    /// </summary>
    public async Task<StatusResumeResult?> ResumeAsync(IWorkflowsDbContext db, WorkflowActivity activity, WorkflowStatus status, JsonElement? signal,
        CancellationToken cancellationToken)
    {
        await using IWorkflowLease<IWorkflowStatusType> lease = source.Lease<IWorkflowStatusType>(services);
        if (Find(lease, activity.StatusType) is not { } type)
        {
            return null;
        }

        WorkflowItemSession item;
        try
        {
            item = (await stores.For(activity.ItemKind).OpenAsSystemAsync(db, activity.ItemId, cancellationToken))?.Item
                ?? throw new InvalidOperationException($"The item waiting in step {activity.Id} is gone.");
        }
        catch (WorkflowItemKindOffException)
        {
            return null; // the item's plugin is off (ADR 0090): it keeps waiting, as for a type whose plugin is off
        }

        JsonElement? state = activity.StateJson is { } json ? JsonDocument.Parse(json).RootElement.Clone() : null;
        // What the step says while it works (an AI step's skill, version and reasons) is a run of its own in the record's
        // workflow history, saved with this change.
        WorkflowTrace trace = new(item.Item.ScopeId ?? Guid.Empty, item.Item.Id, item.Item.Key, activity.WorkflowId, activity.WorkflowVersion, WorkflowRunKinds.Step,
            status.Key, 0, item.Status, item.Status, null) { ItemKind = item.Item.Kind };
        StepContext step = await StepAsync(item, status.Key, activity.Id, state, trace, cancellationToken);
        StatusResumeResult result = await type.ResumeAsync(step, status.Settings, signal, cancellationToken);
        if (result.Exit is { } exit && !Allowed(type, status).Contains(exit))
        {
            logger.LogWarning("Status type {Type} answered with '{Exit}', which isn't one of its exits; it keeps waiting.", activity.StatusType, exit);
            return StatusResumeResult.KeepWaiting(activity.DueAt);
        }

        if (trace.StepCount > 0)
        {
            (WorkflowRun run, List<WorkflowRunStep> steps) = trace.ToRows(WorkflowRunOutcomes.Worked,
                result.Exit is { } left ? $"Left by '{left}'." : "Keeps waiting.", item.Status, null);
            db.WorkflowRuns.Add(run);
            db.WorkflowRunSteps.AddRange(steps);
        }

        return result;
    }

    /// <summary>The component a plugin's status type shows with its waiting step (ADR 0081); null for none, or when its plugin is off.</summary>
    public async Task<string?> PanelAsync(string statusType)
    {
        await using IWorkflowLease<IWorkflowStatusType> lease = source.Lease<IWorkflowStatusType>(services);
        return Find(lease, statusType)?.Panel;
    }

    /// <summary>The exits a status of this type may leave by: its type's, its optional ones, and the routes its settings name.</summary>
    private static IEnumerable<string> Allowed(IWorkflowStatusType type, WorkflowStatus status) =>
        type.Exits.Concat(type.OptionalExits).Concat(WorkflowCatalog.SettingsRoutes(status.Settings, type.RoutesSetting));

    private async Task<StepContext> StepAsync(WorkflowItemSession item, string statusKey, Guid? activityId, JsonElement? state, WorkflowTrace? trace,
        CancellationToken cancellationToken) =>
        new()
        {
            Item = item.Item,
            StatusKey = statusKey,
            ActivityId = activityId,
            State = state,
            Values = await item.ValuesAsync(cancellationToken),
            Session = item,
            Trace = trace,
        };

    private static IWorkflowStatusType? Find(IWorkflowLease<IWorkflowStatusType> lease, string type) =>
        lease.Items.FirstOrDefault(i => WorkflowCatalog.TypeName(i.PluginKey, i.Instance.Key) == type).Instance;

    private sealed class StepContext : WorkflowStatusContext
    {
        public required WorkflowItemSession Session { get; init; }
        public WorkflowTrace? Trace { get; init; }

        public override Task<IReadOnlyDictionary<string, JsonElement?>?> ValuesAsAsync(Guid accountId, CancellationToken cancellationToken) =>
            Session.ValuesAsAsync(accountId, cancellationToken);

        public override Task<bool> IsServiceAccountAsync(Guid accountId, CancellationToken cancellationToken) => Session.IsServiceAccountAsync(accountId, cancellationToken);

        public override T? Feature<T>() where T : class => Session.Feature<T>();

        public override void Log(string message, string level = "info") => Trace?.Note(WorkflowRunPhases.Enter, "log", level == "error" ? WorkflowRunStepOutcomes.Failed : WorkflowRunStepOutcomes.Done,
            null, StatusKey, message);
    }
}
