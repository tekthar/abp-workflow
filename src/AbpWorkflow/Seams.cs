using Microsoft.Extensions.DependencyInjection;

namespace AbpWorkflow;

/// <summary>
/// Where the workflow engine finds extensions and status types: the host's own built-ins and, in a host with
/// plugins, each plugin's, marked with its key. The default has the host's own, from DI; a host with plugins leases theirs.
/// </summary>
public interface IWorkflowExtensionSource
{
    /// <summary>Changes whenever the extensions there are change (a plugin enabled, disabled or upgraded): what's cached from an older one is stale.</summary>
    long Generation { get; }

    /// <summary>
    /// The extensions of a kind, for one use: dispose the lease right after (<c>await using</c>), and never keep an item
    /// past it. A plugin's types must not outlive the call, or the plugin can't unload when it's disabled or upgraded.
    /// </summary>
    IWorkflowLease<T> Lease<T>(IServiceProvider services) where T : class;
}

/// <summary>The extensions leased: the host's own with no plugin key, then each plugin's with its key.</summary>
public interface IWorkflowLease<T> : IAsyncDisposable where T : class
{
    IEnumerable<(string? PluginKey, T Instance)> Items { get; }
}

/// <summary>The default: the host's own extensions from DI, which never change, with nothing to release.</summary>
public sealed class ServiceWorkflowExtensions : IWorkflowExtensionSource
{
    public long Generation => 0;

    public IWorkflowLease<T> Lease<T>(IServiceProvider services) where T : class => new ServiceLease<T>([.. services.GetServices<T>()]);

    private sealed class ServiceLease<T>(List<T> items) : IWorkflowLease<T> where T : class
    {
        public IEnumerable<(string? PluginKey, T Instance)> Items => items.Select(i => ((string?)null, i));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

/// <summary>
/// Who people are, for items whose host doesn't say itself (a plugin's kind): the groups an account is in,
/// whether it's a service account. The default knows nobody; a host asks its own accounts.
/// </summary>
public interface IWorkflowPeople
{
    Task<IReadOnlyList<Guid>> GroupIdsAsync(Guid accountId, CancellationToken cancellationToken);

    Task<bool> IsServiceAccountAsync(Guid accountId, CancellationToken cancellationToken);
}

/// <summary>The default: nobody is in a group, and nobody is a service account.</summary>
public sealed class NoWorkflowPeople : IWorkflowPeople
{
    public Task<IReadOnlyList<Guid>> GroupIdsAsync(Guid accountId, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<Guid>>([]);

    public Task<bool> IsServiceAccountAsync(Guid accountId, CancellationToken cancellationToken) => Task.FromResult(false);
}

/// <summary>
/// Whether the engine's work that asks for a unit of work of its own (moving an item on from a waiting step, a signal)
/// joins the caller's instead: a host whose callers make several changes as one transaction says so while
/// they do, as a host may for a plugin's call. The default never joins.
/// </summary>
public interface IWorkflowJoinSwitch
{
    bool Joining { get; }
}

/// <summary>The default: the engine's own units of work are always its own.</summary>
public sealed class NeverJoins : IWorkflowJoinSwitch
{
    public bool Joining => false;
}

/// <summary>
/// What a workflow's checks look up when it's saved or imported: whether the people, groups and fields its
/// settings name exist in a scope (a project; none for a site workflow). Only the host knows them.
/// </summary>
public interface IWorkflowSiteContexts
{
    IWorkflowSiteContext For(IWorkflowsDbContext db, Guid? scopeId);
}

/// <summary>
/// The default, for a host that gives none: every account and group exists, and a field has the shape an item kind
/// declares for it (<see cref="IWorkflowItemKind.Fields"/>), or is text. A host that knows its people and fields says
/// so with its own.
/// </summary>
public sealed class UncheckedWorkflowSiteContexts(IServiceProvider services) : IWorkflowSiteContexts
{
    public IWorkflowSiteContext For(IWorkflowsDbContext db, Guid? scopeId) => new Site(scopeId, services);

    private sealed class Site(Guid? scopeId, IServiceProvider services) : IWorkflowSiteContext
    {
        public Guid? ScopeId => scopeId;

        public Task<bool> GroupExistsAsync(Guid id, CancellationToken cancellationToken) => Task.FromResult(true);

        public Task<bool> AccountExistsAsync(Guid id, CancellationToken cancellationToken) => Task.FromResult(true);

        public async Task<string?> FieldKindAsync(string field, CancellationToken cancellationToken) =>
            (await services.GetRequiredService<KindItems>().CatalogAsync()).SelectMany(k => k.Fields).FirstOrDefault(f => f.Name == field)?.Kind ?? "text";
    }
}

/// <summary>
/// What the engine tells its host as items move, inside the change that moved them: a transition taken, an
/// item moved on from a waiting step, an approval decided. The default tells no one, which suits a host whose items
/// already say what happened in their own events.
/// </summary>
public interface IWorkflowEvents
{
    Task TransitionTakenAsync(WorkflowTransitionTaken taken, CancellationToken cancellationToken);

    Task ItemAdvancedAsync(WorkflowItemAdvanced advanced, CancellationToken cancellationToken);

    Task ApprovalDecidedAsync(WorkflowApprovalDecided decided, CancellationToken cancellationToken);
}

/// <summary>The default: nobody is told.</summary>
public sealed class NoWorkflowEvents : IWorkflowEvents
{
    public Task TransitionTakenAsync(WorkflowTransitionTaken taken, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task ItemAdvancedAsync(WorkflowItemAdvanced advanced, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task ApprovalDecidedAsync(WorkflowApprovalDecided decided, CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>
/// The engine's error codes, on refusals the person can act on. A host answers them with their statuses (ABP's
/// <c>AbpExceptionHttpStatusCodeOptions</c>, or its own status finder); without that, ABP answers 403 for both.
/// </summary>
public static class WorkflowErrorCodes
{
    /// <summary>A rule or a condition refused it. 403.</summary>
    public const string Forbidden = "AbpWorkflow:Forbidden";

    /// <summary>Someone else changed the item first, or the approval is over. 409.</summary>
    public const string Conflict = "AbpWorkflow:Conflict";
}
