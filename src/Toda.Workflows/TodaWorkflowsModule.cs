using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Volo.Abp.Modularity;

namespace Toda.Workflows;

/// <summary>
/// toda-workflow (ADR 0089): definitions, the engine and runner, status types, approvals, the worker and the tracer. A
/// host maps the engine's tables in its own context (<see cref="WorkflowsModel.ConfigureWorkflows"/>), replaces
/// <see cref="IWorkflowsDbContext"/> with it, and gives a store for each kind of item it moves.
/// </summary>
[DependsOn(typeof(Volo.Abp.EntityFrameworkCore.PostgreSql.AbpEntityFrameworkCorePostgreSqlModule))]
public class TodaWorkflowsModule : AbpModule
{
    public const string Version = "0.1.0";

    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        // The seams' defaults: a host replaces the ones it has its own of, in its module.
        context.Services.TryAddSingleton<IWorkflowExtensionSource, ServiceWorkflowExtensions>();
        context.Services.TryAddTransient<IWorkflowSiteContexts, UncheckedWorkflowSiteContexts>();
        context.Services.TryAddTransient<IWorkflowEvents, NoWorkflowEvents>();
        // Waiting steps advanced in the background, and old runs pruned.
        context.Services.AddHostedService<WorkflowWorker>();
        context.Services.AddHostedService<WorkflowTraceRetention>();
    }
}
