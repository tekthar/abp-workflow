using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Volo.Abp;
using Volo.Abp.DependencyInjection;

namespace Toda.Workflows;

/// <summary>
/// What a workflow extension sees while a transition runs (ADR 0019): the item moving (a session inside the host's
/// change, ADR 0089), the workflow and the transition, and who is taking it. Actions change the item through
/// the session, which records history as the transition's change set and doesn't check the person's field rights: the
/// workflow's author decided the change, not the person taking the transition.
/// </summary>
public sealed class TransitionContext(WorkflowItemSession item, WorkflowDefinition workflow, WorkflowTransition transition, string fromStatus)
{
    public WorkflowItemSession Item { get; } = item;
    public IWorkflowsDbContext Db => Item.Db;
    public Guid ActorId => Item.ActorId;
    public WorkflowDefinition Workflow { get; } = workflow;
    public WorkflowTransition Transition { get; } = transition;
    public string FromStatus { get; } = fromStatus;

    /// <summary>The workflow and published version the item runs, for activities that outlive the request.</summary>
    public Guid? WorkflowId { get; init; }

    public int WorkflowVersion { get; init; }

    /// <summary>The run being traced (ADR 0034); null when nothing is recorded (checking which transitions to offer).</summary>
    public WorkflowTrace? Trace { get; init; }
}

/// <summary>An extension a site has: its type in workflows, kind, texts, settings schema and edit page.</summary>
public sealed record WorkflowExtensionInfo(string Type, string Kind, string Title, string Description, JsonElement SettingsSchema, string? EditComponent, string? PluginKey);

/// <summary>
/// Runs a transition's conditions, checks and actions (ADR 0019): the built-ins, the host's and plugins' through one
/// contract. An extension nobody provides, or one that throws, stops the transition with a message naming it: nothing
/// is skipped silently.
/// </summary>
public class WorkflowExtensions(
    IWorkflowExtensionSource source, IServiceProvider services, IWorkflowSiteContexts sites) : ITransientDependency
{
    public const string BuiltinPrefix = "builtin";

    /// <summary>Traces are stored, not read by one person: their lines are in English.</summary>
    private const string TraceLanguage = "en";

    public async Task<List<WorkflowExtensionInfo>> ListAsync()
    {
        await using IWorkflowLease<IWorkflowExtension> lease = source.Lease<IWorkflowExtension>(services);
        return lease.Items.Select(i => new WorkflowExtensionInfo(TypeOf(i.PluginKey, i.Instance), WorkflowExtensionKinds.Current(i.Instance.Kind), i.Instance.Title, i.Instance.Description,
                JsonDocument.Parse(i.Instance.SettingsSchema).RootElement.Clone(), i.Instance.EditComponent, i.PluginKey))
            .OrderBy(i => i.Kind).ThenBy(i => i.Type).ToList();
    }

    /// <summary>What's wrong with a transition's extensions: types nobody provides, the wrong kind, settings that don't fit.</summary>
    public Task<List<string>> CheckAsync(IWorkflowsDbContext db, Guid? projectId, WorkflowTransition transition, CancellationToken cancellationToken) =>
        CheckUsesAsync(db, projectId, $"Transition '{transition.Key}'",
            Conditions(transition.Conditions).Select(u => (u, WorkflowExtensionKinds.Condition))
                .Concat((transition.Checks ?? []).Select(u => (u, WorkflowExtensionKinds.Check)))
                .Concat((transition.Actions ?? []).Select(u => (u, WorkflowExtensionKinds.Action))), cancellationToken);

    /// <summary>
    /// What in a workflow doesn't work on items of a kind (ADR 0089): extensions and plugins' status types whose
    /// <c>AppliesTo</c> leaves it out, such as a record's children on an asset's workflow. Empty when it all fits.
    /// </summary>
    public async Task<List<string>> NotForKindAsync(WorkflowDefinition definition, string kind, CancellationToken cancellationToken)
    {
        IEnumerable<ExtensionUse> uses = definition.Transitions.SelectMany(t => Conditions(t.Conditions).Concat(t.Checks ?? []).Concat(t.Actions ?? []))
            .Concat(definition.Statuses.SelectMany(s => s.Type switch
            {
                WorkflowCatalog.Automated => WorkflowCatalog.Actions(s.Settings),
                WorkflowCatalog.Decision => WorkflowCatalog.Routes(s.Settings).SelectMany(r => Conditions(r.When)),
                _ => [],
            }));
        List<string> problems = [];
        await using (IWorkflowLease<IWorkflowExtension> lease = source.Lease<IWorkflowExtension>(services))
        {
            foreach (string type in uses.Select(u => u.Type).Distinct())
            {
                if (Find(lease, type)?.AppliesTo is { } kinds && !kinds.Contains(kind))
                {
                    problems.Add($"{type} works on {ExtensionHelpers.List(kinds)} only.");
                }
            }
        }

        await using IWorkflowLease<IWorkflowStatusType> types = source.Lease<IWorkflowStatusType>(services);
        foreach (WorkflowStatus status in definition.Statuses.Where(s => WorkflowCatalog.IsPluginType(s.Type)))
        {
            if (types.Items.FirstOrDefault(i => WorkflowCatalog.TypeName(i.PluginKey, i.Instance.Key) == status.Type).Instance?.AppliesTo is { } kinds && !kinds.Contains(kind))
            {
                problems.Add($"Status '{status.Key}' is a {status.Type}, which works on {ExtensionHelpers.List(kinds)} only.");
            }
        }

        return problems;
    }

    /// <summary>What's wrong with a status's type settings (ADR 0019): actions, routes, approvers, times.</summary>
    public async Task<List<string>> CheckStatusAsync(IWorkflowsDbContext db, Guid? projectId, WorkflowStatus status, CancellationToken cancellationToken)
    {
        string where = $"Status '{status.Key}'";
        IWorkflowSiteContext site = sites.For(db, projectId);
        List<string> problems = [];
        switch (status.Type)
        {
            case WorkflowCatalog.Automated:
                return await CheckUsesAsync(db, projectId, where, WorkflowCatalog.Actions(status.Settings).Select(u => (u, WorkflowExtensionKinds.Action)), cancellationToken);
            case WorkflowCatalog.Decision:
            {
                List<(string Exit, ConditionGroup? When)> routes = WorkflowCatalog.Routes(status.Settings);
                if (routes.Count == 0)
                {
                    problems.Add($"{where} decides between routes; give it at least one (settings.routes: [{{exit, when}}]).");
                }

                problems.AddRange(routes.GroupBy(r => r.Exit).Where(g => g.Count() > 1 || g.Key == "otherwise").Select(g => $"{where} has route '{g.Key}' more than once, or named 'otherwise'."));
                problems.AddRange(await CheckUsesAsync(db, projectId, where, routes.SelectMany(r => Conditions(r.When)).Select(u => (u, WorkflowExtensionKinds.Condition)), cancellationToken));
                return problems;
            }

            case WorkflowCatalog.Wait:
                if (StatusEntry.Int(status.Settings, "minutes") is { } minutes)
                {
                    if (minutes < 1)
                    {
                        problems.Add($"{where} waits at least a minute.");
                    }
                }
                else if (StatusEntry.Str(status.Settings, "field") is { } field)
                {
                    if (field is not "dueDate" and not "startDate" && await site.FieldKindAsync(field, cancellationToken) is not "date")
                    {
                        problems.Add($"{where} waits until a date field; '{field}' isn't one.");
                    }
                }
                else
                {
                    problems.Add($"{where} waits for minutes or until a date field (settings.minutes or settings.field).");
                }

                return problems;
            case WorkflowCatalog.Approval:
            {
                List<string> accountIds = StatusEntry.Strings(status.Settings, "accounts"), groupIds = StatusEntry.Strings(status.Settings, "groups");
                string? field = StatusEntry.Str(status.Settings, "field");
                foreach (string a in accountIds)
                {
                    if (!Guid.TryParse(a, out Guid id) || !await site.AccountExistsAsync(id, cancellationToken))
                    {
                        problems.Add($"{where}: there's no account {a}.");
                    }
                }

                foreach (string g in groupIds)
                {
                    if (!Guid.TryParse(g, out Guid id) || !await site.GroupExistsAsync(id, cancellationToken))
                    {
                        problems.Add($"{where}: there's no group {g}.");
                    }
                }

                if (field is not null && field is not ("assignee" or "reporter") && await site.FieldKindAsync(field, cancellationToken) is not ("person" or "people"))
                {
                    problems.Add($"{where} takes approvers from a people field; '{field}' isn't one.");
                }

                if (accountIds.Count + groupIds.Count == 0 && field is null)
                {
                    problems.Add($"{where} needs approvers: accounts, groups or a people field.");
                }

                if (StatusEntry.Str(status.Settings, "rule") is { } rule && rule is not ("any" or "all" or "count"))
                {
                    problems.Add($"{where}'s rule is any, all or count.");
                }

                if (StatusEntry.Int(status.Settings, "timeoutMinutes") is < 1)
                {
                    problems.Add($"{where} times out after at least a minute.");
                }

                if (StatusEntry.Str(status.Settings, "timeoutField") is { } timeoutField && timeoutField != "dueDate"
                    && await site.FieldKindAsync(timeoutField, cancellationToken) is not "date")
                {
                    problems.Add($"{where} times out on a date field's day; '{timeoutField}' isn't one.");
                }

                return problems;
            }

            case var type when WorkflowCatalog.IsPluginType(type):
            {
                await using IWorkflowLease<IWorkflowStatusType> lease = source.Lease<IWorkflowStatusType>(services);
                IWorkflowStatusType? plugin = lease.Items.FirstOrDefault(i => WorkflowCatalog.TypeName(i.PluginKey, i.Instance.Key) == type).Instance;
                if (plugin is null)
                {
                    problems.Add($"{where} has type '{type}', which isn't available here.");
                    return problems;
                }

                List<string> shape = WorkflowSchema.Check(plugin.SettingsSchema, status.Settings, $"{where} settings");
                problems.AddRange(shape);
                if (shape.Count == 0)
                {
                    problems.AddRange((await plugin.CheckSettingsAsync(status.Settings, site, cancellationToken)).Select(p => $"{where}: {p}"));
                }

                return problems;
            }

            default:
                return problems;
        }
    }

    private async Task<List<string>> CheckUsesAsync(IWorkflowsDbContext db, Guid? projectId, string owner, IEnumerable<(ExtensionUse Use, string Kind)> uses, CancellationToken cancellationToken)
    {
        await using IWorkflowLease<IWorkflowExtension> lease = source.Lease<IWorkflowExtension>(services);
        IWorkflowSiteContext site = sites.For(db, projectId);
        List<string> problems = [];
        foreach ((ExtensionUse use, string kind) in uses)
        {
            string where = $"{owner}: {use.Type}";
            IWorkflowExtension? extension = Find(lease, use.Type);
            if (extension is null)
            {
                problems.Add($"{where} isn't available here.");
                continue;
            }

            if (WorkflowExtensionKinds.Current(extension.Kind) != kind)
            {
                problems.Add($"{where} is {Article(WorkflowExtensionKinds.Current(extension.Kind))}, not {Article(kind)}.");
                continue;
            }

            if (extension.AppliesTo is { } kinds && site.ItemKind is { } itemKind && !kinds.Contains(itemKind))
            {
                problems.Add($"{where} works on {ExtensionHelpers.List(kinds)} only, not on {itemKind}s.");
                continue;
            }

            List<string> shape = WorkflowSchema.Check(extension.SettingsSchema, use.Settings, $"{where} settings");
            problems.AddRange(shape);
            if (shape.Count == 0)
            {
                problems.AddRange((await extension.CheckSettingsAsync(use.Settings, site, cancellationToken)).Select(p => $"{where}: {p}"));
            }
        }

        return problems;
    }

    /// <summary>
    /// Null when the conditions hold; otherwise why the transition can't be taken. Traced as <paramref name="phase"/>
    /// steps whose paths start with <paramref name="path"/> ("2" puts the conditions of a decision's second route at
    /// "2.1", "2.2").
    /// </summary>
    public async Task<string?> ConditionsAsync(ConditionGroup? conditions, TransitionContext context, CancellationToken cancellationToken,
        string phase = WorkflowRunPhases.Condition, string? statusKey = null, string path = "")
    {
        if (conditions is null || (conditions.Conditions.Count == 0 && (conditions.Groups ?? []).Count == 0))
        {
            return null;
        }

        await using IWorkflowLease<IWorkflowExtension> lease = source.Lease<IWorkflowExtension>(services);
        HostContext host = await HostContext.CreateAsync(context, cancellationToken);
        return await GroupAsync(lease, conditions, host, new Tracing(context.Trace, phase, statusKey), path, cancellationToken);
    }

    /// <summary>Where a step goes in the trace: the run, its phase and the status it belongs to.</summary>
    private sealed record Tracing(WorkflowTrace? Trace, string Phase, string? StatusKey);

    private async Task<string?> GroupAsync(IWorkflowLease<IWorkflowExtension> lease, ConditionGroup group, HostContext host, Tracing tracing, string path,
        CancellationToken cancellationToken)
    {
        bool any = group.Match == ConditionGroup.Any;
        List<string> reasons = [];
        string Child(int index) => path.Length == 0 ? index.ToString(System.Globalization.CultureInfo.InvariantCulture) : $"{path}.{index}";
        int count = group.Conditions.Count;
        IEnumerable<Func<Task<string?>>> checks = group.Conditions
            .Select((use, i) => (Func<Task<string?>>)(() => EvaluateAsync(lease, use, host, tracing, Child(i + 1), cancellationToken)))
            .Concat((group.Groups ?? []).Select((g, i) => (Func<Task<string?>>)(() => NestedAsync(lease, g, host, tracing, Child(count + i + 1), cancellationToken))));
        foreach (Func<Task<string?>> check in checks)
        {
            string? reason = await check();
            if (reason is null && any)
            {
                return null;
            }

            if (reason is not null)
            {
                if (!any)
                {
                    return reason;
                }

                reasons.Add(reason);
            }
        }

        return any ? string.Join(" Or: ", reasons) : null;
    }

    /// <summary>A group inside a group: a step of its own, around its conditions' steps.</summary>
    private async Task<string?> NestedAsync(IWorkflowLease<IWorkflowExtension> lease, ConditionGroup group, HostContext host, Tracing tracing, string path,
        CancellationToken cancellationToken)
    {
        WorkflowTrace.Step? step = tracing.Trace?.Begin(tracing.Phase, "group", group.Match == ConditionGroup.Any ? "any" : "all", tracing.StatusKey, path);
        string? reason = await GroupAsync(lease, group, host, tracing, path, cancellationToken);
        if (step is not null)
        {
            tracing.Trace!.End(step, reason is null ? WorkflowRunStepOutcomes.Passed : WorkflowRunStepOutcomes.Failed, reason);
        }

        return reason;
    }

    /// <summary>Every check's complaint, all of them at once; empty when the record may move on.</summary>
    public async Task<List<string>> CheckAsync(IReadOnlyList<ExtensionUse> checks, TransitionContext context, CancellationToken cancellationToken)
    {
        if (checks.Count == 0)
        {
            return [];
        }

        await using IWorkflowLease<IWorkflowExtension> lease = source.Lease<IWorkflowExtension>(services);
        HostContext host = await HostContext.CreateAsync(context, cancellationToken);
        List<string> problems = [];
        Tracing tracing = new(context.Trace, WorkflowRunPhases.Check, null);
        foreach (ExtensionUse use in checks)
        {
            if (await EvaluateAsync(lease, use, host, tracing, null, cancellationToken) is { } problem)
            {
                problems.Add(problem);
            }
        }

        return problems;
    }

    /// <summary>A transition's actions, or an automated status's (<paramref name="statusKey"/> says which).</summary>
    public async Task RunAsync(IReadOnlyList<ExtensionUse> actions, TransitionContext context, CancellationToken cancellationToken,
        string phase = WorkflowRunPhases.Action, string? statusKey = null)
    {
        if (actions.Count == 0)
        {
            return;
        }

        await using IWorkflowLease<IWorkflowExtension> lease = source.Lease<IWorkflowExtension>(services);
        foreach (ExtensionUse use in actions)
        {
            IWorkflowExtension? extension = Find(lease, use.Type);
            WorkflowTrace.Step? step = context.Trace?.Begin(phase, use.Type, Describe(extension, use), statusKey);
            if (extension is null)
            {
                throw new WorkflowFailureException(Missing(use));
            }

            HostContext host = await HostContext.CreateAsync(context, cancellationToken); // fresh values after earlier actions
            await Guard(use, () => extension.ExecuteAsync(host, use.Settings, cancellationToken));
            if (step is not null)
            {
                context.Trace!.End(step, WorkflowRunStepOutcomes.Done);
            }
        }
    }

    private static async Task<string?> EvaluateAsync(IWorkflowLease<IWorkflowExtension> lease, ExtensionUse use, HostContext host, Tracing tracing, string? path,
        CancellationToken cancellationToken)
    {
        IWorkflowExtension? extension = Find(lease, use.Type);
        WorkflowTrace.Step? step = tracing.Trace?.Begin(tracing.Phase, use.Type, Describe(extension, use), tracing.StatusKey, path);
        if (extension is null)
        {
            throw new WorkflowFailureException(Missing(use));
        }

        string? result = null;
        await Guard(use, async () => result = await extension.EvaluateAsync(host, use.Settings, cancellationToken));
        if (step is not null)
        {
            tracing.Trace!.End(step, result is null ? WorkflowRunStepOutcomes.Passed : WorkflowRunStepOutcomes.Failed, result);
        }

        return result;
    }

    /// <summary>The extension's one line about its settings, in English (traces are stored, not read by one person).</summary>
    private static string Describe(IWorkflowExtension? extension, ExtensionUse use)
    {
        if (extension is null)
        {
            return use.Type;
        }

        try
        {
            string summary = extension.Summary(use.Settings, TraceLanguage);
            return string.IsNullOrWhiteSpace(summary) ? extension.Title : $"{extension.Title}: {summary}";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return extension.Title;
        }
    }

    /// <summary>A failing extension stops the transition with a message naming it.</summary>
    private static async Task Guard(ExtensionUse use, Func<Task> run)
    {
        try
        {
            await run();
        }
        catch (Exception ex) when (ex is not UserFriendlyException and not OperationCanceledException)
        {
            throw new WorkflowFailureException($"The workflow step '{use.Type}' failed: {ex.Message}");
        }
    }

    private static IWorkflowExtension? Find(IWorkflowLease<IWorkflowExtension> lease, string type) =>
        lease.Items.FirstOrDefault(i => TypeOf(i.PluginKey, i.Instance) == type).Instance;

    private static string Article(string kind) => kind.Length > 0 && "aeiou".Contains(kind[0]) ? $"an {kind}" : $"a {kind}";

    private static string TypeOf(string? pluginKey, IWorkflowExtension extension) => WorkflowCatalog.TypeName(pluginKey, extension.Key);

    private static string Missing(ExtensionUse use) => $"The workflow uses '{use.Type}', which isn't available here. Ask an admin to enable it or take it out of the workflow.";

    private static IEnumerable<ExtensionUse> Conditions(ConditionGroup? group) =>
        group is null ? [] : group.Conditions.Concat((group.Groups ?? []).SelectMany(Conditions));

    /// <summary>The context handed to extensions, over a running transition's item.</summary>
    private sealed class HostContext : WorkflowExtensionContext
    {
        private TransitionContext _transition = null!;

        public static async Task<HostContext> CreateAsync(TransitionContext transition, CancellationToken cancellationToken) =>
            new()
            {
                Item = transition.Item.Item,
                FromStatus = transition.FromStatus,
                ToStatus = transition.Transition.To,
                Transition = transition.Transition.Key,
                ActorId = transition.ActorId,
                Values = await transition.Item.ValuesAsync(cancellationToken),
                Before = await transition.Item.BeforeAsync(cancellationToken),
                _transition = transition,
            };

        public override Task<IReadOnlyList<Guid>> ActorGroupIdsAsync(CancellationToken cancellationToken) => _transition.Item.ActorGroupIdsAsync(cancellationToken);

        public override Task<bool> ActorManagesScopeAsync(CancellationToken cancellationToken) => _transition.Item.ActorManagesScopeAsync(cancellationToken);

        public override Task<bool> ActorHoldsAssignmentsOfAsync(Guid accountId, CancellationToken cancellationToken) =>
            _transition.Item.ActorHoldsAssignmentsOfAsync(accountId, cancellationToken);

        public override T? Feature<T>() where T : class => _transition.Item.Feature<T>();

        public override Task SetAsync(string field, JsonElement? value, CancellationToken cancellationToken) =>
            _transition.Item.SetFieldAsync(field, value, cancellationToken);

        public override void Log(string message, string level = "info") => _transition.Trace?.Log(level, message);
    }
}
