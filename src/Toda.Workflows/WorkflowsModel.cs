using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Volo.Abp.Data;
using Volo.Abp.DependencyInjection;
using Volo.Abp.EntityFrameworkCore;
using Volo.Abp.Uow;

namespace Toda.Workflows;

/// <summary>
/// The workflow engine's tables, as a host has them in its own context (ADR 0089, S5b), the way ABP's modules ship
/// theirs: its queries can join them (RQL's <c>approver = currentUser()</c> reads waiting approvals beside records), one
/// change writes the item and the engine's rows in one transaction, and the host's migrations make them. The host's
/// context says it replaces this one (<c>[ReplaceDbContext]</c>), so the engine's provider hands out the host's.
/// </summary>
[ConnectionStringName("Default")]
public interface IWorkflowsDbContext : IEfCoreDbContext
{
    DbSet<Workflow> Workflows { get; }
    DbSet<WorkflowVersion> WorkflowVersions { get; }
    DbSet<WorkflowActivity> WorkflowActivities { get; }
    DbSet<ApprovalDecision> ApprovalDecisions { get; }
    DbSet<WorkflowRun> WorkflowRuns { get; }
    DbSet<WorkflowRunStep> WorkflowRunSteps { get; }
}

/// <summary>
/// Runs the engine's work in a unit of work: the caller's when there is one, or a new one (a host's change it joins,
/// the worker's own). The context is the host's, as it replaces <see cref="IWorkflowsDbContext"/>.
/// </summary>
public class WorkflowsDb(IUnitOfWorkManager unitOfWorkManager, IDbContextProvider<IWorkflowsDbContext> provider) : ITransientDependency
{
#pragma warning disable CA1068 // the token before isTransactional, as the hosts' module helpers have it
    public async Task<T> RunAsync<T>(Func<IWorkflowsDbContext, Task<T>> work, bool requiresNew = false, CancellationToken cancellationToken = default,
        bool isTransactional = false)
    {
        using IUnitOfWork uow = unitOfWorkManager.Begin(requiresNew: requiresNew, isTransactional: isTransactional);
        IWorkflowsDbContext db = await provider.GetDbContextAsync();
        T result = await work(db);
        await uow.CompleteAsync(cancellationToken);
        return result;
    }

    public Task RunAsync(Func<IWorkflowsDbContext, Task> work, bool requiresNew = false, CancellationToken cancellationToken = default, bool isTransactional = false) =>
        RunAsync<bool>(async db => { await work(db); return true; }, requiresNew, cancellationToken, isTransactional);
#pragma warning restore CA1068
}

/// <summary>The engine's model: <c>builder.ConfigureWorkflows()</c> in a host context's <c>OnModelCreating</c>.</summary>
public static class WorkflowsModel
{
    /// <summary>The engine's schema, apart from any host's (ADR 0089).</summary>
    public const string Schema = "workflows";

    /// <summary>How the engine stores its JSON (definitions, names): its own contract, the format number inside (ADR 0089).</summary>
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static void ConfigureWorkflows(this ModelBuilder builder)
    {
        builder.Entity<Workflow>(b =>
        {
            b.ToTable("workflows", Schema);
            b.Property(x => x.Key).HasMaxLength(64).IsRequired();
            b.Property(x => x.Names).HasJsonConversion();
            b.Property(x => x.Description).HasMaxLength(2000);
            b.Property(x => x.Definition).HasJsonConversion();
            b.Property(x => x.Draft).HasConversion(
                    v => v == null ? null : JsonSerializer.Serialize(v, Json),
                    v => v == null ? null : JsonSerializer.Deserialize<WorkflowDefinition>(v, Json),
                    new ValueComparer<WorkflowDefinition?>(
                        (a, b) => JsonSerializer.Serialize(a, Json) == JsonSerializer.Serialize(b, Json),
                        v => JsonSerializer.Serialize(v, Json).GetHashCode(),
                        v => v == null ? null : JsonSerializer.Deserialize<WorkflowDefinition>(JsonSerializer.Serialize(v, Json), Json)))
                .HasColumnType("jsonb");
            b.HasIndex(x => new { x.ScopeId, x.Key }).IsUnique().AreNullsDistinct(false);
        });

        builder.Entity<WorkflowActivity>(b =>
        {
            b.ToTable("workflow_activities", Schema);
            b.Property(x => x.ItemKind).HasMaxLength(16).IsRequired().HasDefaultValue(WorkflowItemKinds.Record);
            b.Property(x => x.StatusKey).HasMaxLength(64).IsRequired();
            b.Property(x => x.StatusType).HasMaxLength(128).IsRequired();
            b.Property(x => x.State).HasMaxLength(16).IsRequired();
            b.Property(x => x.Outcome).HasMaxLength(64);
            b.Property(x => x.Rule).HasMaxLength(16);
            b.Property(x => x.LastError).HasMaxLength(2000);
            b.Property(x => x.StateJson).HasColumnType("jsonb");
            b.HasIndex(x => new { x.State, x.NextSweepAt });
            b.HasIndex(x => new { x.ItemId, x.EnteredAt });
        });

        builder.Entity<WorkflowRun>(b =>
        {
            b.ToTable("workflow_runs", Schema);
            b.Property(x => x.ItemKind).HasMaxLength(16).IsRequired().HasDefaultValue(WorkflowItemKinds.Record);
            b.Property(x => x.ItemKey).HasMaxLength(64).IsRequired();
            b.Property(x => x.Kind).HasMaxLength(16).IsRequired();
            b.Property(x => x.Transition).HasMaxLength(128).IsRequired();
            b.Property(x => x.FromStatus).HasMaxLength(64).IsRequired();
            b.Property(x => x.ToStatus).HasMaxLength(64).IsRequired();
            b.Property(x => x.EndStatus).HasMaxLength(64);
            b.Property(x => x.Outcome).HasMaxLength(16).IsRequired();
            b.Property(x => x.Message).HasMaxLength(2000);
            b.Property(x => x.ActingMode).HasMaxLength(32);
            b.HasIndex(x => new { x.WorkflowId, x.WorkflowVersion, x.StartedAt });
            b.HasIndex(x => new { x.ItemId, x.StartedAt });
            b.HasIndex(x => x.StartedAt);
        });

        builder.Entity<WorkflowRunStep>(b =>
        {
            b.ToTable("workflow_run_steps", Schema);
            b.Property(x => x.Phase).HasMaxLength(16).IsRequired();
            b.Property(x => x.StatusKey).HasMaxLength(64);
            b.Property(x => x.Type).HasMaxLength(256).IsRequired();
            b.Property(x => x.Label).HasMaxLength(500);
            b.Property(x => x.Path).HasMaxLength(64);
            b.Property(x => x.Outcome).HasMaxLength(16).IsRequired();
            b.Property(x => x.Message).HasMaxLength(2000);
            b.Property(x => x.Logs).HasJsonConversion();
            b.HasOne<WorkflowRun>().WithMany().HasForeignKey(x => x.RunId).OnDelete(DeleteBehavior.Cascade);
            b.HasIndex(x => new { x.RunId, x.Seq });
            b.HasIndex(x => x.At);
        });

        builder.Entity<ApprovalDecision>(b =>
        {
            b.ToTable("approval_decisions", Schema);
            b.Property(x => x.Comment).HasMaxLength(4000);
            b.Property(x => x.ActingMode).HasMaxLength(32);
            b.HasIndex(x => new { x.ActivityId, x.AccountId }).IsUnique();
        });

        builder.Entity<WorkflowVersion>(b =>
        {
            b.ToTable("workflow_versions", Schema);
            b.Property(x => x.Definition).HasJsonConversion();
            b.HasIndex(x => new { x.WorkflowId, x.Version }).IsUnique();
        });
    }

    /// <summary>Stores the value as jsonb, compared by its JSON so changes inside lists are detected.</summary>
    private static PropertyBuilder<T> HasJsonConversion<T>(this PropertyBuilder<T> property) where T : class?
    {
        property.HasConversion(
                v => JsonSerializer.Serialize(v, Json),
                v => JsonSerializer.Deserialize<T>(v, Json)!,
                new ValueComparer<T>(
                    (a, b) => JsonSerializer.Serialize(a, Json) == JsonSerializer.Serialize(b, Json),
                    v => JsonSerializer.Serialize(v, Json).GetHashCode(),
                    v => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(v, Json), Json)!))
            .HasColumnType("jsonb")
            .IsRequired();
        return property;
    }
}
