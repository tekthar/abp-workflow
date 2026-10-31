using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Volo.Abp;
using Volo.Abp.DependencyInjection;

namespace Toda.Workflows;

/// <summary>
/// Drafts, publishing and bindings of workflows (ADR 0018). A draft is checked for shape when saved (numbers, keys,
/// references) and for safety when published (exits lead somewhere, no status removed while records sit in it).
/// </summary>
public partial class WorkflowService(WorkflowCatalog catalog, WorkflowExtensions extensions, WorkflowItemStores stores) : ITransientDependency
{
    /// <summary>
    /// Checks a draft against the published definition and gives new statuses and transitions (number 0) their numbers.
    /// Keys and numbers that were published never change; names, categories and everything else may.
    /// </summary>
    /// <summary>The designer's diagram (positions) is small; this keeps a broken client from storing megabytes.</summary>
    private const int MaxDiagramLength = 256 * 1024;

    public async Task<WorkflowDefinition> NormalizeAsync(IWorkflowsDbContext db, Workflow workflow, WorkflowDefinition draft, CancellationToken cancellationToken)
    {
        if (draft.Format > Toda.Workflows.WorkflowFormat.Current)
        {
            throw new UserFriendlyException($"This workflow is in format {draft.Format}, from a newer server; this one reads format {Toda.Workflows.WorkflowFormat.Current}.");
        }

        if (draft.Statuses.Count == 0)
        {
            throw new UserFriendlyException("A workflow needs at least one status.");
        }

        List<string> problems = [];
        List<WorkflowStatus> statuses = [];
        foreach (WorkflowStatus s in draft.Statuses)
        {
            if (!WorkflowKeys.Key().IsMatch(s.Key ?? ""))
            {
                problems.Add($"Status key '{s.Key}' must be lowercase letters, digits and dashes.");
                continue;
            }

            if (statuses.Any(x => x.Key == s.Key))
            {
                problems.Add($"Two statuses have the key '{s.Key}'.");
                continue;
            }

            WorkflowStatus? published = workflow.Definition.Statuses.FirstOrDefault(p => p.Key == s.Key);
            if (published is null && s.Number > 0 && workflow.Definition.Statuses.FirstOrDefault(p => p.Number == s.Number) is { } other)
            {
                problems.Add($"Status {s.Number} is '{other.Key}'; a published status keeps its key. Give '{s.Key}' number 0 to add it.");
                continue;
            }

            // A new status keeps the number an earlier save of this draft gave it.
            int number = published?.Number ?? (Reserved(s.Number, workflow.LastStatusNumber) && statuses.All(x => x.Number != s.Number) ? s.Number : 0);

            if ((s.Names ?? []).Count == 0 || s.Names!.Values.All(string.IsNullOrWhiteSpace))
            {
                problems.Add($"Status '{s.Key}' needs a name.");
            }

            if (!catalog.StatusTypes.ContainsKey(s.Type ?? WorkflowStatus.Manual))
            {
                problems.Add($"Status '{s.Key}' has type '{s.Type}', which isn't available here. Types: {string.Join(", ", catalog.StatusTypes.Keys.Order())}.");
            }

            problems.AddRange(await extensions.CheckStatusAsync(db, workflow.ScopeId, s, cancellationToken));
            statuses.Add(s with
            {
                Number = number == 0 ? ++workflow.LastStatusNumber : number,
                Names = (s.Names ?? []).Where(n => !string.IsNullOrWhiteSpace(n.Value)).ToDictionary(n => n.Key, n => n.Value.Trim()),
                Type = s.Type ?? WorkflowStatus.Manual,
            });
        }

        HashSet<string> keys = statuses.Select(s => s.Key).ToHashSet(StringComparer.Ordinal);
        foreach (WorkflowStatus s in statuses)
        {
            foreach ((string outcome, string target) in s.Exits ?? [])
            {
                if (!keys.Contains(target))
                {
                    problems.Add($"Status '{s.Key}' leaves by '{outcome}' to '{target}', which isn't a status here.");
                }
            }
        }

        List<WorkflowTransition> transitions = [];
        foreach (WorkflowTransition t in draft.Transitions)
        {
            if (!WorkflowKeys.Key().IsMatch(t.Key ?? ""))
            {
                problems.Add($"Transition key '{t.Key}' must be lowercase letters, digits and dashes.");
                continue;
            }

            if (transitions.Any(x => x.Key == t.Key))
            {
                problems.Add($"Two transitions have the key '{t.Key}'.");
                continue;
            }

            WorkflowTransition? published = workflow.Definition.Transitions.FirstOrDefault(p => p.Key == t.Key);
            if (published is null && t.Number > 0 && workflow.Definition.Transitions.FirstOrDefault(p => p.Number == t.Number) is { } other)
            {
                problems.Add($"Transition {t.Number} is '{other.Key}'; a published transition keeps its key. Give '{t.Key}' number 0 to add it.");
                continue;
            }

            foreach (string from in (t.From ?? []).Where(f => !keys.Contains(f)))
            {
                problems.Add($"Transition '{t.Key}' starts at '{from}', which isn't a status here.");
            }

            if (!keys.Contains(t.To ?? ""))
            {
                problems.Add($"Transition '{t.Key}' leads to '{t.To}', which isn't a status here.");
            }

            if (t.From is { Count: 0 })
            {
                problems.Add($"Transition '{t.Key}' starts nowhere: give it statuses, or make it global (from: null).");
            }

            if ((t.Names ?? []).Count == 0 || t.Names!.Values.All(string.IsNullOrWhiteSpace))
            {
                problems.Add($"Transition '{t.Key}' needs a name.");
            }

            problems.AddRange(await extensions.CheckAsync(db, workflow.ScopeId, t, cancellationToken));
            transitions.Add(t with
            {
                Number = published?.Number
                    ?? (Reserved(t.Number, workflow.LastTransitionNumber) && transitions.All(x => x.Number != t.Number) ? t.Number : ++workflow.LastTransitionNumber),
                Names = (t.Names ?? []).Where(n => !string.IsNullOrWhiteSpace(n.Value)).ToDictionary(n => n.Key, n => n.Value.Trim()),
                From = t.From?.Distinct().ToList(),
                Checks = t.Checks ?? [],
                Actions = t.Actions ?? [],
                Fields = t.Fields ?? [],
            });
        }

        if (draft.Initial is { } initial && !keys.Contains(initial))
        {
            problems.Add($"The initial status '{initial}' isn't a status here.");
        }

        if (problems.Count > 0)
        {
            throw new UserFriendlyException(string.Join(" ", problems.Distinct()));
        }

        if (draft.Diagram is { } diagram && diagram.ToJsonString().Length > MaxDiagramLength)
        {
            throw new UserFriendlyException($"The workflow's diagram is larger than {MaxDiagramLength / 1024} KB.");
        }

        return draft with { Format = Toda.Workflows.WorkflowFormat.Current, Statuses = statuses, Transitions = transitions };
    }

    private static bool Reserved(int number, int last) => number > 0 && number <= last;

    /// <summary>
    /// Publishes the draft: checked again (statuses records sit in can't go; asynchronous exits must lead somewhere),
    /// then made the definition every bound type runs, as the next version.
    /// </summary>
    public async Task PublishAsync(IWorkflowsDbContext db, Workflow workflow, Guid actorId, CancellationToken cancellationToken)
    {
        WorkflowDefinition draft = workflow.Draft ?? throw new UserFriendlyException($"{workflow.Key} has no draft to publish.");
        draft = await NormalizeAsync(db, workflow, draft, cancellationToken);
        List<string> problems = await PublishProblemsAsync(db, workflow, draft, cancellationToken);
        if (problems.Count > 0)
        {
            throw new UserFriendlyException(string.Join(" ", problems));
        }

        workflow.Definition = draft;
        workflow.Draft = null;
        workflow.DraftUpdatedAt = null;
        await PublishedAsync(db, workflow, actorId, cancellationToken);
    }

    /// <summary>
    /// For configuration as code: what's wrong with replacing a workflow's published definition with this one (shape,
    /// extensions, exits, statuses records sit in). Checks a copy; the workflow itself isn't changed.
    /// </summary>
    public async Task<(WorkflowDefinition? Normalized, List<string> Problems)> CheckReplacementAsync(IWorkflowsDbContext db, Workflow? existing, Guid? projectId, string key,
        WorkflowDefinition definition, CancellationToken cancellationToken)
    {
        Workflow probe = existing is null
            ? new Workflow(Guid.Empty, projectId, key, new WorkflowDefinition())
            : new Workflow(existing.Id, existing.ScopeId, existing.Key, existing.Definition) { LastStatusNumber = existing.LastStatusNumber, LastTransitionNumber = existing.LastTransitionNumber };
        probe.LastStatusNumber = Math.Max(probe.LastStatusNumber, definition.Statuses.Select(s => s.Number).DefaultIfEmpty(0).Max());
        probe.LastTransitionNumber = Math.Max(probe.LastTransitionNumber, definition.Transitions.Select(t => t.Number).DefaultIfEmpty(0).Max());
        try
        {
            WorkflowDefinition normalized = await NormalizeAsync(db, probe, definition, cancellationToken);
            return (normalized, await PublishProblemsAsync(db, probe, normalized, cancellationToken));
        }
        catch (UserFriendlyException ex)
        {
            return (null, [ex.Message]);
        }
    }

    /// <summary>Replaces a workflow's published definition (already checked) as its next version.</summary>
    public async Task ReplaceAsync(IWorkflowsDbContext db, Workflow workflow, WorkflowDefinition definition, Guid actorId, CancellationToken cancellationToken)
    {
        if (System.Text.Json.JsonSerializer.Serialize(workflow.Definition) == System.Text.Json.JsonSerializer.Serialize(definition))
        {
            return;
        }

        workflow.Definition = definition;
        workflow.LastStatusNumber = Math.Max(workflow.LastStatusNumber, definition.Statuses.Select(s => s.Number).DefaultIfEmpty(0).Max());
        workflow.LastTransitionNumber = Math.Max(workflow.LastTransitionNumber, definition.Transitions.Select(t => t.Number).DefaultIfEmpty(0).Max());
        await PublishedAsync(db, workflow, actorId, cancellationToken);
    }

    /// <summary>Stores the workflow's current definition as its next published version.</summary>
    public static void SaveVersion(IWorkflowsDbContext db, Workflow workflow, Guid? publishedBy)
    {
        workflow.Version++;
        workflow.UpdatedAt = DateTime.UtcNow;
        db.WorkflowVersions.Add(new WorkflowVersion(Guid.NewGuid(), workflow.Id, workflow.Version, workflow.Definition, publishedBy));
    }

    /// <summary>The workflow's current definition stored as its next published version, and what keeps a copy of its statuses told.</summary>
    private async Task PublishedAsync(IWorkflowsDbContext db, Workflow workflow, Guid actorId, CancellationToken cancellationToken)
    {
        SaveVersion(db, workflow, actorId);
        foreach (IWorkflowItemStore store in await stores.AllAsync(db, cancellationToken))
        {
            await store.PublishedAsync(db, workflow, cancellationToken);
        }
    }

    private async Task<List<string>> PublishProblemsAsync(IWorkflowsDbContext db, Workflow workflow, WorkflowDefinition draft, CancellationToken cancellationToken)
    {
        List<string> problems = [];
        foreach (WorkflowStatus status in draft.Statuses)
        {
            foreach (string outcome in catalog.RequiredExits(status).Where(o => !(status.Exits ?? []).ContainsKey(o)))
            {
                problems.Add($"Status '{status.Key}' ({status.Type}) needs a status to go to when '{outcome}'.");
            }
        }

        List<string> removed = workflow.Definition.Statuses.Select(s => s.Key).Except(draft.Statuses.Select(s => s.Key)).ToList();
        if (removed.Count > 0)
        {
            foreach (IWorkflowItemStore store in await stores.AllAsync(db, cancellationToken))
            {
                IReadOnlyDictionary<string, int> sitting;
                try
                {
                    sitting = await store.InStatusesAsync(db, workflow.Id, removed, cancellationToken);
                }
                catch (WorkflowItemKindOffException ex)
                {
                    // A plugin's items this workflow moves can't be counted while it's off (ADR 0090).
                    problems.Add($"Statuses can't be removed while '{ex.Kind}' is off: its {store.Noun}s may be in them. Switch its plugin on first.");
                    continue;
                }

                problems.AddRange(sitting.Select(u =>
                    $"{(u.Value == 1 ? $"1 {store.Noun} is" : $"{u.Value} {store.Noun}s are")} in '{u.Key}', so it can't be removed yet. Move them to another status first."));
            }
        }

        return problems;
    }
}
