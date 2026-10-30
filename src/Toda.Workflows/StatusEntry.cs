using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Volo.Abp;
using Volo.Abp.DependencyInjection;

namespace Toda.Workflows;

/// <summary>
/// What happens as a record enters a status (ADR 0019). Manual: nothing. Automated and decision: run inside the
/// request and leave by an exit at once. Wait and approval: write an activity row (the outbox) that the worker and the
/// approvers finish later. A plugin's type (ADR 0022) leaves at once or writes one that the worker or a signal resumes. A chain of synchronous statuses is guarded: it may pass through at most <see cref="MaxHops"/>.
/// </summary>
public class StatusEntry(WorkflowCatalog catalog, WorkflowExtensions extensions, PluginStatusTypes pluginTypes) : ITransientDependency
{
    public const int MaxHops = 20;

    /// <summary>
    /// Whether entering the status does something: every type that's available here but a plain (manual) one. A type
    /// whose plugin isn't enabled does nothing on a new record, as before it was entered at all.
    /// </summary>
    public bool Works(WorkflowStatus status) =>
        catalog.StatusTypes.TryGetValue(status.Type, out StatusTypeInfo? info) && info.Mode != StatusTypeInfo.ManualMode;

    /// <summary>The component a waiting step shows with: its plugin type's own, while it waits (ADR 0081); null otherwise.</summary>
    public async Task<string?> PanelAsync(string statusType, string state) =>
        state == WorkflowActivityStates.Pending && statusType.Contains(':', StringComparison.Ordinal) ? await pluginTypes.PanelAsync(statusType) : null;

    /// <summary>The record has just moved into <paramref name="status"/> (a different status than before).</summary>
    public async Task EnterAsync(TransitionContext context, WorkflowStatus status, int hops, CancellationToken cancellationToken)
    {
        // Whatever the record was waiting for in the status it left is over.
        foreach (WorkflowActivity open in await context.Db.WorkflowActivities
                     .Where(a => a.ItemId == context.Item.Item.Id && a.State == WorkflowActivityStates.Pending).ToListAsync(cancellationToken))
        {
            open.State = WorkflowActivityStates.Abandoned;
            open.CompletedAt = DateTime.UtcNow;
        }

        WorkflowTrace? trace = context.Trace;
        if (trace is not null)
        {
            trace.Hops = Math.Max(trace.Hops, hops);
        }

        WorkflowTrace.Step? entry = trace?.Begin(WorkflowRunPhases.Enter, status.Type, null, status.Key);
        void Entered(Guid? activityId = null)
        {
            if (entry is not null)
            {
                entry.ActivityId = activityId;
                trace!.End(entry, WorkflowRunStepOutcomes.Done);
            }
        }

        switch (status.Type)
        {
            case WorkflowCatalog.Automated:
                Entered();
                await extensions.RunAsync(WorkflowCatalog.Actions(status.Settings), context, cancellationToken, WorkflowRunPhases.Action, status.Key);
                await LeaveAsync(context, status, "done", hops, cancellationToken);
                return;
            case WorkflowCatalog.Decision:
                Entered();
                int index = 0;
                foreach ((string exit, ConditionGroup? when) in WorkflowCatalog.Routes(status.Settings))
                {
                    string path = (++index).ToString(System.Globalization.CultureInfo.InvariantCulture);
                    WorkflowTrace.Step? route = trace?.Begin(WorkflowRunPhases.Route, "route", exit, status.Key, path);
                    string? why = await extensions.ConditionsAsync(when, context, cancellationToken, WorkflowRunPhases.Route, status.Key, path);
                    if (route is not null)
                    {
                        trace!.End(route, why is null ? WorkflowRunStepOutcomes.Passed : WorkflowRunStepOutcomes.Failed, why);
                    }

                    if (why is null)
                    {
                        await LeaveAsync(context, status, exit, hops, cancellationToken);
                        return;
                    }
                }

                await LeaveAsync(context, status, "otherwise", hops, cancellationToken);
                return;
            case WorkflowCatalog.Wait:
                WorkflowActivity wait = await WaitAsync(context, status, cancellationToken);
                context.Db.WorkflowActivities.Add(wait);
                Entered(wait.Id); // the tracer reads its due time from the activity, in the reader's language
                return;
            case WorkflowCatalog.Approval:
                WorkflowActivity approval = await ApprovalAsync(context, status, cancellationToken);
                context.Db.WorkflowActivities.Add(approval);
                Entered(approval.Id);
                return;
            case var type when WorkflowCatalog.IsPluginType(type):
            {
                // A plugin's type (ADR 0022): it leaves at once, or waits for a time or a signal.
                StatusEntryResult result = await pluginTypes.EnterAsync(context, status, cancellationToken)
                    ?? throw new WorkflowFailureException($"Status '{status.Key}' has type '{status.Type}', which isn't available here. Ask an admin to enable its plugin.");
                if (result.Exit is { } exit)
                {
                    Entered();
                    await LeaveAsync(context, status, exit, hops, cancellationToken);
                    return;
                }

                WorkflowActivity step = NewActivity(context, status);
                step.DueAt = result.WaitUntil;
                step.NextSweepAt = result.WaitUntil ?? DateTime.UtcNow.AddYears(100); // only a signal ends it
                step.StateJson = result.State?.GetRawText();
                context.Db.WorkflowActivities.Add(step);
                Entered(step.Id);
                return;
            }

            default:
                if (!catalog.StatusTypes.ContainsKey(status.Type))
                {
                    throw new WorkflowFailureException($"Status '{status.Key}' has type '{status.Type}', which isn't available here. Ask an admin to enable it.");
                }

                Entered();
                return; // manual
        }
    }

    /// <summary>Moves the record through one of <paramref name="from"/>'s exits, as part of the same change, and enters the target.</summary>
    public async Task LeaveAsync(TransitionContext context, WorkflowStatus from, string outcome, int hops, CancellationToken cancellationToken)
    {
        if (hops >= MaxHops)
        {
            throw new WorkflowFailureException($"The workflow passed through {MaxHops} statuses without stopping (last: {from.Key}). Check its automated and decision statuses for a loop.");
        }

        string targetKey = (from.Exits ?? []).GetValueOrDefault(outcome)
            ?? throw new WorkflowFailureException($"Status '{from.Key}' has no status to go to when '{outcome}'.");
        WorkflowStatus target = context.Workflow.Status(targetKey)
            ?? throw new WorkflowFailureException($"Status '{from.Key}' leads to '{targetKey}' when '{outcome}', which the workflow no longer has.");
        context.Trace?.Note(WorkflowRunPhases.Exit, "exit", WorkflowRunStepOutcomes.Done, outcome, from.Key, $"To {target.Key}");
        context.Item.SetStatus(target);
        await EnterAsync(context, target, hops + 1, cancellationToken);
    }

    private async Task<WorkflowActivity> WaitAsync(TransitionContext context, WorkflowStatus status, CancellationToken cancellationToken)
    {
        WorkflowActivity activity = NewActivity(context, status);
        if (Int(status.Settings, "minutes") is { } minutes)
        {
            activity.DueAt = DateTime.UtcNow.AddMinutes(minutes);
        }
        else if (Str(status.Settings, "field") is { } field)
        {
            string? day = Text(await context.Item.ValuesAsync(cancellationToken), field);
            activity.DueAt = DateOnly.TryParse(day, out DateOnly date)
                ? date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc)
                : throw new UserFriendlyException($"{context.Item.Item.Key} waits in '{status.Key}' until its {field}, which is empty. Fill it in first.");
        }

        activity.NextSweepAt = activity.DueAt ?? DateTime.UtcNow;
        return activity;
    }

    private async Task<WorkflowActivity> ApprovalAsync(TransitionContext context, WorkflowStatus status, CancellationToken cancellationToken)
    {
        WorkflowActivity activity = NewActivity(context, status);
        HashSet<Guid> accounts = [.. Strings(status.Settings, "accounts").Select(a => Guid.TryParse(a, out Guid id) ? id : Guid.Empty).Where(id => id != Guid.Empty)];
        IReadOnlyDictionary<string, JsonElement?> values = await context.Item.ValuesAsync(cancellationToken);
        if (Str(status.Settings, "field") is { } field && values.GetValueOrDefault(field) is { } v)
        {
            // A people field holds account ids: one (the assignee, the reporter, a person field) or a list.
            IEnumerable<string?> ids = v.ValueKind == JsonValueKind.Array ? v.EnumerateArray().Select(i => i.ValueKind == JsonValueKind.String ? i.GetString() : null)
                : [v.ValueKind == JsonValueKind.String ? v.GetString() : null];
            accounts.UnionWith(ids.Select(s => Guid.TryParse(s, out Guid id) ? (Guid?)id : null).OfType<Guid>());
        }

        List<Guid> groups = Strings(status.Settings, "groups").Select(g => Guid.TryParse(g, out Guid id) ? id : Guid.Empty).Where(id => id != Guid.Empty).Distinct().ToList();
        int slots = accounts.Count + groups.Count;
        if (slots == 0)
        {
            throw new UserFriendlyException($"Nobody can approve {context.Item.Item.Key} in '{status.Key}': its approvers' field is empty. Fill it in first.");
        }

        string rule = Str(status.Settings, "rule") ?? "any";
        activity.ApproverAccountIds = [.. accounts];
        activity.ApproverGroupIds = groups;
        activity.Rule = rule;
        activity.Required = rule switch
        {
            "all" => slots,
            "count" => Math.Clamp(Int(status.Settings, "count") ?? 1, 1, slots),
            _ => 1,
        };
        if (Int(status.Settings, "timeoutMinutes") is { } timeout)
        {
            activity.DueAt = DateTime.UtcNow.AddMinutes(timeout);
            activity.NextSweepAt = activity.DueAt.Value;
        }
        else if (Str(status.Settings, "timeoutField") is { } timeoutField
            && DateOnly.TryParse(Text(values, timeoutField), out DateOnly day))
        {
            // Times out as that day starts (UTC); an empty date means no timeout.
            activity.DueAt = day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
            activity.NextSweepAt = activity.DueAt.Value;
        }
        else
        {
            activity.NextSweepAt = DateTime.UtcNow.AddYears(100); // only a decision finishes it
        }

        return activity;
    }

    private static WorkflowActivity NewActivity(TransitionContext context, WorkflowStatus status) =>
        new(Guid.NewGuid(), context.Item.Item.Id, context.WorkflowId ?? Guid.Empty, context.WorkflowVersion, status.Key, status.Type,
            context.ActorId == Guid.Empty ? null : context.ActorId)
        {
            ItemKind = context.Item.Item.Kind,
        };

    /// <summary>A value as text (a date, an id), or null when it's empty or not text.</summary>
    private static string? Text(IReadOnlyDictionary<string, JsonElement?> values, string field) =>
        values.GetValueOrDefault(field) is { ValueKind: JsonValueKind.String } v ? v.GetString() : null;

    internal static string? Str(JsonElement? settings, string name) =>
        settings is { ValueKind: JsonValueKind.Object } s && s.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    internal static int? Int(JsonElement? settings, string name) =>
        settings is { ValueKind: JsonValueKind.Object } s && s.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int i) ? i : null;

    internal static List<string> Strings(JsonElement? settings, string name) =>
        settings is { ValueKind: JsonValueKind.Object } s && s.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Where(i => i.ValueKind == JsonValueKind.String).Select(i => i.GetString()!).ToList()
            : [];
}
