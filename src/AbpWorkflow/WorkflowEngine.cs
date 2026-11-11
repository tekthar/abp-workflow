using Volo.Abp;

namespace AbpWorkflow;

/// <summary>
/// Which transitions a record can take from its status, and which one a request means. With any to any on,
/// every other status is reachable through an implicit transition that has no extensions.
/// </summary>
public static class WorkflowEngine
{
    /// <summary>The transitions out of a status: drawn ones first, then the implicit any-to-any ones to statuses no drawn transition reaches.</summary>
    public static List<WorkflowTransition> Available(WorkflowDefinition definition, string from)
    {
        List<WorkflowTransition> drawn = definition.Transitions
            .Where(t => t.From is null ? t.To != from : t.From.Contains(from))
            .Where(t => definition.Status(t.To) is not null)
            .ToList();
        if (!definition.AnyToAny)
        {
            return drawn;
        }

        IEnumerable<WorkflowTransition> implicitOnes = definition.Statuses
            .Where(s => s.Key != from && drawn.All(t => t.To != s.Key))
            .Select(s => Implicit(s));
        return [.. drawn, .. implicitOnes];
    }

    /// <summary>An any-to-any move to a status: key "to:{status}", no number, no extensions.</summary>
    public static WorkflowTransition Implicit(WorkflowStatus target) => new(0, ImplicitPrefix + target.Key, new Dictionary<string, string>(target.Names), null, target.Key);

    public const string ImplicitPrefix = "to:";

    public static bool IsImplicit(WorkflowTransition transition) => transition.Number == 0;

    /// <summary>
    /// The transition a request means: named by key or number, or the one way to the status asked for. Null when the
    /// status asked for is the current one (nothing to do).
    /// </summary>
    public static WorkflowTransition? Resolve(WorkflowDefinition definition, string from, string? transition, string? target, string typeName)
    {
        List<WorkflowTransition> available = Available(definition, from);
        string Listing() => available.Count == 0
            ? "none: no transition leaves this status"
            : string.Join(", ", available.Select(t => $"{t.Name()} (to {definition.Status(t.To)?.Name() ?? t.To})"));
        if (transition is not null)
        {
            WorkflowTransition? named = available.FirstOrDefault(t => t.Key == transition || (t.Number > 0 && t.Number.ToString() == transition));
            if (named is null)
            {
                bool exists = definition.Transitions.Any(t => t.Key == transition || t.Number.ToString() == transition);
                throw new UserFriendlyException(exists
                    ? $"'{transition}' can't be taken from {definition.Status(from)?.Name() ?? from}. From here: {Listing()}."
                    : $"{typeName} has no transition '{transition}'. From here: {Listing()}.");
            }

            if (target is not null && target != named.To)
            {
                throw new UserFriendlyException($"'{named.Name()}' leads to {named.To}, not {target}.");
            }

            return named;
        }

        if (target is null || target == from)
        {
            return null;
        }

        if (definition.Status(target) is null)
        {
            throw new UserFriendlyException($"{typeName} has no status '{target}'.");
        }

        List<WorkflowTransition> ways = available.Where(t => t.To == target).ToList();
        return ways.Count switch
        {
            1 => ways[0],
            0 => throw new UserFriendlyException($"No transition leads from {definition.Status(from)?.Name() ?? from} to {definition.Status(target)!.Name()}. From here: {Listing()}."),
            _ => throw new UserFriendlyException($"Several transitions lead to {definition.Status(target)!.Name()}: {string.Join(", ", ways.Select(w => w.Key))}. Name one."),
        };
    }
}
