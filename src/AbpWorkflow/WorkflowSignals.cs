using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Volo.Abp.DependencyInjection;

namespace AbpWorkflow;

/// <summary>
/// Resumes plugins' waiting steps: when their time comes (the worker) or a signal arrives (a plugin, or a
/// customer's reply). A step is resumed under a lock on its activity row, so a signal and the timer never both run it.
/// One that comes to an exit moves the record at once; if that fails, the worker tries again.
/// </summary>
[ExposeServices(typeof(IWorkflowSignals), typeof(WorkflowSignals))]
public class WorkflowSignals(WorkflowsDb workflowsDb, PluginStatusTypes types, WorkflowRunner runner, ILogger<WorkflowSignals> logger)
    : IWorkflowSignals, ITransientDependency
{
    /// <summary>How long a step whose plugin is off waits before it's looked at again.</summary>
    public static readonly TimeSpan PluginOffRetry = TimeSpan.FromMinutes(5);

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1847", Justification = "In a query: Npgsql translates Contains(string), not Contains(char).")]
    public async Task<IReadOnlyList<PendingStep>> PendingAsync(Guid itemId, CancellationToken cancellationToken = default) =>
        await workflowsDb.RunAsync(db => db.WorkflowActivities.AsNoTracking()
            .Where(a => a.ItemId == itemId && a.State == WorkflowActivityStates.Pending && a.StatusType.Contains(":"))
            .Select(a => new PendingStep(a.Id, a.ItemId, a.StatusType, a.StatusKey)).ToListAsync(cancellationToken), cancellationToken: cancellationToken);

    public Task<bool> SignalAsync(Guid activityId, JsonElement payload, CancellationToken cancellationToken = default) =>
        ResumeAsync(activityId, payload.Clone(), cancellationToken);

    /// <summary>
    /// Resumes a waiting step: with the signal, or (null) because its time came. True when it came to an exit the
    /// record moves by; false when it keeps waiting, isn't waiting, or its plugin is off.
    /// </summary>
    public async Task<bool> ResumeAsync(Guid activityId, JsonElement? signal, CancellationToken cancellationToken)
    {
        bool completed = await workflowsDb.RunAsync(async db =>
        {
            WorkflowActivity? activity = await db.WorkflowActivities
                .FromSqlInterpolated($"""SELECT * FROM workflows.workflow_activities WHERE "Id" = {activityId} FOR UPDATE""").FirstOrDefaultAsync(cancellationToken);
            if (activity is null || activity.State != WorkflowActivityStates.Pending || !WorkflowCatalog.IsPluginType(activity.StatusType))
            {
                return false;
            }

            DateTime now = DateTime.UtcNow;
            if (signal is null && activity.DueAt is { } due && due > now)
            {
                activity.NextSweepAt = due; // woken early: not its time yet
                await db.SaveChangesAsync(cancellationToken);
                return false;
            }

            WorkflowDefinition? definition = await db.WorkflowVersions.AsNoTracking()
                .Where(v => v.WorkflowId == activity.WorkflowId && v.Version == activity.WorkflowVersion).Select(v => v.Definition).FirstOrDefaultAsync(cancellationToken);
            WorkflowStatus? status = definition?.Status(activity.StatusKey);
            if (status is null)
            {
                activity.State = WorkflowActivityStates.Abandoned;
                activity.LastError = $"The workflow no longer has status '{activity.StatusKey}'.";
                await db.SaveChangesAsync(cancellationToken);
                return false;
            }

            StatusResumeResult? result = await types.ResumeAsync(db, activity, status, signal, cancellationToken);
            if (result is null)
            {
                activity.LastError = $"The status type '{activity.StatusType}' or the item's kind '{activity.ItemKind}' isn't available: its plugin may be off. "
                    + "The item stays here until it's back.";
                activity.NextSweepAt = now + PluginOffRetry;
                await db.SaveChangesAsync(cancellationToken);
                return false;
            }

            activity.LastError = null;
            if (result.Exit is { } exit)
            {
                activity.CompletedAt = now;
                activity.NextSweepAt = now;
                if ((status.Exits ?? []).ContainsKey(exit))
                {
                    activity.State = WorkflowActivityStates.Completed;
                    activity.Outcome = exit;
                }
                else
                {
                    activity.State = WorkflowActivityStates.TimedOut; // an optional exit the status doesn't map: it stays, and says so
                    activity.Outcome = exit;
                }
            }
            else
            {
                // Keep waiting: until the new time, or the old one when a signal didn't change it; with none, only a signal ends it.
                activity.DueAt = result.WaitUntil ?? (signal is null ? null : activity.DueAt);
                activity.NextSweepAt = activity.DueAt ?? now.AddYears(100);
                if (result.State is { } state)
                {
                    activity.StateJson = state.GetRawText();
                }
            }

            await db.SaveChangesAsync(cancellationToken);
            return activity.State == WorkflowActivityStates.Completed;
        }, requiresNew: true, cancellationToken, isTransactional: true);

        if (completed)
        {
            try
            {
                await runner.AdvanceAsync(activityId, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Moving the record on from step {Activity} failed; the worker tries again.", activityId);
            }
        }

        return completed;
    }
}
