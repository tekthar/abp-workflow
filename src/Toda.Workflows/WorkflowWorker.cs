using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Toda.Workflows;

/// <summary>
/// The worker and sweeper for asynchronous statuses (ADR 0019), every two seconds:
/// waits whose time has come complete with "done"; approvals past their timeout leave by "timedOut" when the status has
/// that exit, or stay and say so; plugins' steps whose time came are resumed (ADR 0022); completed visits whose record wasn't moved yet are advanced (retried with a growing
/// pause, then marked failed). Rows are claimed by pushing <see cref="WorkflowActivity.NextSweepAt"/> forward with a
/// conditional update, so two nodes never work on the same one. This is the only place timeouts happen.
/// </summary>
public class WorkflowWorker(IServiceScopeFactory scopes, ILogger<WorkflowWorker> logger) : BackgroundService
{
    public const int MaxAttempts = 5;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using PeriodicTimer timer = new(TimeSpan.FromSeconds(2));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await SweepAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "The workflow sweep failed; it runs again shortly.");
            }
        }
    }

    public async Task SweepAsync(CancellationToken cancellationToken)
    {
        await using AsyncServiceScope scope = scopes.CreateAsyncScope();
        WorkflowsDb workflowsDb = scope.ServiceProvider.GetRequiredService<WorkflowsDb>();
        WorkflowRunner runner = scope.ServiceProvider.GetRequiredService<WorkflowRunner>();
        WorkflowSignals signals = scope.ServiceProvider.GetRequiredService<WorkflowSignals>();
        DateTime now = DateTime.UtcNow;
        List<Guid> due = await workflowsDb.RunAsync(db => db.WorkflowActivities.AsNoTracking()
            .Where(a => (a.State == WorkflowActivityStates.Pending || a.State == WorkflowActivityStates.Completed) && a.NextSweepAt <= now)
            .OrderBy(a => a.NextSweepAt).Select(a => a.Id).Take(100).ToListAsync(cancellationToken), requiresNew: true, cancellationToken: cancellationToken);
        foreach (Guid id in due)
        {
            if (await ClaimAsync(workflowsDb, id, cancellationToken) is not { } activity)
            {
                continue; // another node took it
            }

            if (activity.State == WorkflowActivityStates.Pending && WorkflowCatalog.IsPluginType(activity.StatusType))
            {
                // A plugin's step (ADR 0022): its time came; resuming moves the record when it comes to an exit.
                await signals.ResumeAsync(id, null, cancellationToken);
                continue;
            }

            if (activity.State == WorkflowActivityStates.Pending)
            {
                await FinishPendingAsync(workflowsDb, activity, cancellationToken);
                if (await workflowsDb.RunAsync(db => db.WorkflowActivities.AsNoTracking().FirstAsync(a => a.Id == id, cancellationToken), requiresNew: true, cancellationToken: cancellationToken)
                    is not { State: WorkflowActivityStates.Completed })
                {
                    continue;
                }
            }

            try
            {
                await runner.AdvanceAsync(id, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await workflowsDb.RunAsync(async db =>
                {
                    WorkflowActivity row = await db.WorkflowActivities.FirstAsync(a => a.Id == id, cancellationToken);
                    row.Attempts++;
                    row.LastError = ex.Message.Length > 2000 ? ex.Message[..2000] : ex.Message;
                    row.State = row.Attempts >= MaxAttempts ? WorkflowActivityStates.Failed : row.State;
                    row.NextSweepAt = DateTime.UtcNow.AddSeconds(30 * Math.Pow(2, row.Attempts));
                    await db.SaveChangesAsync(cancellationToken);
                }, requiresNew: true, cancellationToken: cancellationToken);
                logger.LogWarning("Moving a record on from {Status} failed (attempt {Attempt}): {Error}", activity.StatusKey, activity.Attempts + 1, ex.Message);
            }
        }
    }

    /// <summary>Takes a row for a minute by moving its next sweep forward, if nobody else just did.</summary>
    private static Task<WorkflowActivity?> ClaimAsync(WorkflowsDb workflowsDb, Guid id, CancellationToken cancellationToken) =>
        workflowsDb.RunAsync<WorkflowActivity?>(async db =>
        {
            DateTime now = DateTime.UtcNow;
            int claimed = await db.WorkflowActivities.Where(a => a.Id == id && a.NextSweepAt <= now)
                .ExecuteUpdateAsync(s => s.SetProperty(a => a.NextSweepAt, now.AddMinutes(1)), cancellationToken);
            return claimed == 1 ? await db.WorkflowActivities.AsNoTracking().FirstAsync(a => a.Id == id, cancellationToken) : null;
        }, requiresNew: true, cancellationToken: cancellationToken);

    /// <summary>A pending visit whose time has come: a wait completes; an approval times out.</summary>
    private static Task FinishPendingAsync(WorkflowsDb workflowsDb, WorkflowActivity claimed, CancellationToken cancellationToken) =>
        workflowsDb.RunAsync(async db =>
        {
            WorkflowActivity activity = await db.WorkflowActivities.FirstAsync(a => a.Id == claimed.Id, cancellationToken);
            if (activity.State != WorkflowActivityStates.Pending || activity.DueAt is not { } dueAt || dueAt > DateTime.UtcNow)
            {
                activity.NextSweepAt = activity.DueAt ?? DateTime.UtcNow.AddYears(100);
                await db.SaveChangesAsync(cancellationToken);
                return;
            }

            activity.CompletedAt = DateTime.UtcNow;
            activity.NextSweepAt = DateTime.UtcNow;
            if (activity.StatusType == WorkflowCatalog.Wait)
            {
                activity.State = WorkflowActivityStates.Completed;
                activity.Outcome = "done";
            }
            else if (await HasExitAsync(db, activity, "timedOut", cancellationToken))
            {
                activity.State = WorkflowActivityStates.Completed;
                activity.Outcome = "timedOut"; // a timeout never takes the success exit
            }
            else
            {
                activity.State = WorkflowActivityStates.TimedOut; // stays, and the record says so
            }

            await db.SaveChangesAsync(cancellationToken);
        }, requiresNew: true, cancellationToken: cancellationToken);

    private static async Task<bool> HasExitAsync(IWorkflowsDbContext db, WorkflowActivity activity, string outcome, CancellationToken cancellationToken)
    {
        WorkflowDefinition? definition = await db.WorkflowVersions.AsNoTracking()
            .Where(v => v.WorkflowId == activity.WorkflowId && v.Version == activity.WorkflowVersion).Select(v => v.Definition).FirstOrDefaultAsync(cancellationToken);
        return definition?.Status(activity.StatusKey)?.Exits?.ContainsKey(outcome) == true;
    }
}
