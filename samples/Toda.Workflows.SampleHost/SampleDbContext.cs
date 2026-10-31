using Microsoft.EntityFrameworkCore;
using Volo.Abp.Data;
using Volo.Abp.DependencyInjection;
using Volo.Abp.Domain.Entities;
using Volo.Abp.EntityFrameworkCore;
using Volo.Abp.Uow;

namespace Toda.Workflows.SampleHost;

/// <summary>
/// A document: the sample's own item, in its own table. Its status and version are the host's to keep; the engine asks
/// for them through <see cref="DocumentKind"/> and never touches the table.
/// </summary>
public class Document : Entity<Guid>
{
    protected Document()
    {
    }

    public Document(Guid id, string key, string type, string title, Guid ownerId, string status) : base(id)
    {
        Key = key;
        Type = type;
        Title = title;
        OwnerId = ownerId;
        Status = status;
        Version = 1;
    }

    /// <summary>What people call it: "DOC-1".</summary>
    public string Key { get; private set; } = null!;

    /// <summary>Its type ("memo", "contract"): each type is bound to a workflow of its own.</summary>
    public string Type { get; private set; } = null!;

    public string Title { get; set; } = null!;
    public string? Body { get; set; }
    public decimal? Amount { get; set; }
    public Guid OwnerId { get; private set; }
    public Guid? ReviewerId { get; set; }

    /// <summary>The key of its workflow's status.</summary>
    public string Status { get; set; } = null!;

    /// <summary>Bumped on every change, so a stale write is refused.</summary>
    public int Version { get; set; }
}

/// <summary>
/// The sample's context: its documents and the engine's tables, in one database and one change. It replaces the
/// engine's <see cref="IWorkflowsDbContext"/>, so the engine writes its rows (approvals, runs, waiting steps) in the
/// same transaction as the document.
/// </summary>
[ConnectionStringName("Default")]
[ReplaceDbContext(typeof(IWorkflowsDbContext))]
public class SampleDbContext(DbContextOptions<SampleDbContext> options) : AbpDbContext<SampleDbContext>(options), IWorkflowsDbContext
{
    public DbSet<Document> Documents => Set<Document>();

    public DbSet<Workflow> Workflows => Set<Workflow>();
    public DbSet<WorkflowVersion> WorkflowVersions => Set<WorkflowVersion>();
    public DbSet<WorkflowActivity> WorkflowActivities => Set<WorkflowActivity>();
    public DbSet<ApprovalDecision> ApprovalDecisions => Set<ApprovalDecision>();
    public DbSet<WorkflowRun> WorkflowRuns => Set<WorkflowRun>();
    public DbSet<WorkflowRunStep> WorkflowRunSteps => Set<WorkflowRunStep>();
    public DbSet<WorkflowBinding> WorkflowBindings => Set<WorkflowBinding>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        // The engine's tables, in schema "workflows".
        modelBuilder.ConfigureWorkflows();
        modelBuilder.Entity<Document>(b =>
        {
            b.ToTable("documents");
            b.Property(d => d.Key).HasMaxLength(64);
            b.Property(d => d.Type).HasMaxLength(64);
            b.Property(d => d.Status).HasMaxLength(64);
            b.Property(d => d.Title).HasMaxLength(200);
            b.HasIndex(d => d.Key).IsUnique();
            b.HasIndex(d => new { d.Type, d.Status });
        });
    }
}

/// <summary>Runs work in a unit of work with the sample's context, as the engine's <see cref="WorkflowsDb"/> does with its own.</summary>
public class SampleDb(IUnitOfWorkManager unitOfWorkManager, IDbContextProvider<SampleDbContext> provider) : ITransientDependency
{
    public async Task<T> RunAsync<T>(Func<SampleDbContext, Task<T>> work, CancellationToken cancellationToken = default)
    {
        using IUnitOfWork uow = unitOfWorkManager.Begin(requiresNew: true, isTransactional: true);
        T result = await work(await provider.GetDbContextAsync());
        await uow.CompleteAsync(cancellationToken);
        return result;
    }
}
