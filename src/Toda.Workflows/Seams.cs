using Microsoft.Extensions.DependencyInjection;

namespace Toda.Workflows;

/// <summary>
/// Where the workflow engine finds extensions and status types (ADR 0089): the host's own built-ins and, in a host with
/// plugins, each plugin's, marked with its key. The default has the host's own, from DI; Toda leases its plugins'.
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
/// What a workflow's checks look up when it's saved or imported (ADR 0089): whether the people, groups and fields its
/// settings name exist in a scope (a project; none for a site workflow). Only the host knows them.
/// </summary>
public interface IWorkflowSiteContexts
{
    IWorkflowSiteContext For(IWorkflowsDbContext db, Guid? scopeId);
}

/// <summary>
/// The default, for a host that gives none: every account and group exists, and every field is text. Settings that
/// need a date or a people field are then refused; a host that has them says so with its own.
/// </summary>
public sealed class UncheckedWorkflowSiteContexts : IWorkflowSiteContexts
{
    public IWorkflowSiteContext For(IWorkflowsDbContext db, Guid? scopeId) => new Site(scopeId);

    private sealed class Site(Guid? scopeId) : IWorkflowSiteContext
    {
        public Guid? ScopeId => scopeId;

        public Task<bool> GroupExistsAsync(Guid id, CancellationToken cancellationToken) => Task.FromResult(true);

        public Task<bool> AccountExistsAsync(Guid id, CancellationToken cancellationToken) => Task.FromResult(true);

        public Task<string?> FieldKindAsync(string field, CancellationToken cancellationToken) => Task.FromResult<string?>("text");
    }
}

/// <summary>
/// What the engine tells its host as items move (ADR 0089), inside the change that moved them: a transition taken, an
/// item moved on from a waiting step, an approval decided. The default tells no one; Toda's records and assets say what
/// happened in their own events, so it keeps the default.
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
/// The engine's error codes, on refusals the person can act on. Toda's strings for now (its web and phone app read
/// them); the app's rename moves both together.
/// </summary>
public static class WorkflowErrorCodes
{
    /// <summary>A rule or a condition refused it. 403.</summary>
    public const string Forbidden = "Toda:Forbidden";

    /// <summary>Someone else changed the item first, or the approval is over. 409.</summary>
    public const string Conflict = "Toda:Conflict";
}
