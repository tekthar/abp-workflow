using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Volo.Abp;
using Volo.Abp.DependencyInjection;

namespace Toda.Workflows;

/// <summary>
/// Writes transition runs (ADR 0034): with the record's change when it goes through, on their own when it's refused.
/// Writing a trace never changes what happened to the record; a failure here is logged and swallowed.
/// </summary>
public class WorkflowTraces(WorkflowsDb workflowsDb, ILogger<WorkflowTraces> logger) : ITransientDependency
{
    /// <summary>
    /// Runs a change that may start a trace (<paramref name="trace"/> reads it once the change has made it). When the
    /// change commits, so did the run with it; when it's refused or fails, the run is written on its own.
    /// </summary>
    public async Task<T> GuardAsync<T>(Func<WorkflowTrace?> trace, Func<Task<T>> change)
    {
        try
        {
            T result = await change();
            if (trace() is { } done)
            {
                done.Saved = true;
            }

            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException && trace() is { } stopped)
        {
            await SaveStoppedAsync(stopped, ex);
            throw;
        }
    }

    /// <summary>Adds the run to the change being saved; it commits or rolls back with it.</summary>
    public static void Attach(IWorkflowsDbContext db, WorkflowTrace trace, string? endStatus, Guid? changeSetId)
    {
        (WorkflowRun run, List<WorkflowRunStep> steps) = trace.ToRows(WorkflowRunOutcomes.Moved, null, endStatus, changeSetId);
        db.WorkflowRuns.Add(run);
        db.WorkflowRunSteps.AddRange(steps);
    }

    /// <summary>The change didn't happen: writes the run as refused or failed, in a transaction of its own.</summary>
    public async Task SaveStoppedAsync(WorkflowTrace trace, Exception stopped)
    {
        if (trace.Saved)
        {
            return;
        }

        string outcome = stopped is not WorkflowFailureException and (UserFriendlyException or BusinessException) ? WorkflowRunOutcomes.Refused : WorkflowRunOutcomes.Failed;
        try
        {
            (WorkflowRun run, List<WorkflowRunStep> steps) = trace.ToRows(outcome, stopped.Message, null, null);
            await workflowsDb.RunAsync(async db =>
            {
                db.WorkflowRuns.Add(run);
                db.WorkflowRunSteps.AddRange(steps);
                await db.SaveChangesAsync();
            }, requiresNew: true);
            trace.Saved = true;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Couldn't write the trace of a stopped transition on {RecordKey}.", trace.RecordKey);
        }
    }
}

/// <summary>Deletes old traces every hour, in batches: steps after <c>StepDays</c> (30), runs after <c>RunDays</c> (365).</summary>
public class WorkflowTraceRetention(IServiceScopeFactory scopes, IConfiguration configuration, ILogger<WorkflowTraceRetention> logger) : BackgroundService
{
    private const int Batch = 5000;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using PeriodicTimer timer = new(TimeSpan.FromHours(1));
        do
        {
            try
            {
                await PruneAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Pruning workflow traces failed; it runs again in an hour.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    public async Task PruneAsync(CancellationToken cancellationToken)
    {
        int stepDays = configuration.GetValue("Toda:Workflows:Trace:StepDays", 30);
        int runDays = configuration.GetValue("Toda:Workflows:Trace:RunDays", 365);
        DateTime stepsBefore = DateTime.UtcNow.AddDays(-stepDays);
        DateTime runsBefore = DateTime.UtcNow.AddDays(-runDays);
        await using AsyncServiceScope scope = scopes.CreateAsyncScope();
        WorkflowsDb workflowsDb = scope.ServiceProvider.GetRequiredService<WorkflowsDb>();
        while (await workflowsDb.RunAsync(db => db.WorkflowRunSteps.Where(s => s.At < stepsBefore).OrderBy(s => s.At).Take(Batch)
                   .ExecuteDeleteAsync(cancellationToken), requiresNew: true, cancellationToken: cancellationToken) == Batch)
        {
        }

        while (await workflowsDb.RunAsync(db => db.WorkflowRuns.Where(r => r.StartedAt < runsBefore).OrderBy(r => r.StartedAt).Take(Batch)
                   .ExecuteDeleteAsync(cancellationToken), requiresNew: true, cancellationToken: cancellationToken) == Batch)
        {
        }
    }
}
