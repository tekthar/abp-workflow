using System.Text.RegularExpressions;

namespace AbpWorkflow;

/// <summary>
/// The name of a workflow, status or transition in a list of languages, most wanted first: the first non-empty name in
/// that order, else any name at all, else the key, so something readable always shows. English without a list.
/// </summary>
public static class WorkflowNames
{
    public const string Source = "en";

    public static string Pick(IReadOnlyDictionary<string, string>? names, string key, IReadOnlyList<string>? languages = null)
    {
        if (names is null || names.Count == 0)
        {
            return key;
        }

        foreach (string code in languages ?? [Source])
        {
            if (names.TryGetValue(code, out string? value) && !string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return names.Values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? key;
    }
}

/// <summary>
/// A row of the engine's that says who acted for the account it names, when that was someone else: a decision
/// or a run. The host fills it as it fills its own such rows; the engine sets it only for a delegate's decision.
/// </summary>
public interface IWorkflowActed
{
    Guid? ActingAccountId { get; set; }

    /// <summary>How they acted for them: <see cref="WorkflowActingModes.Delegation"/>, or a mode of the host's.</summary>
    string? ActingMode { get; set; }
}

public static class WorkflowActingModes
{
    /// <summary>A delegate decided for the approver they hold approvals of.</summary>
    public const string Delegation = "delegation";
}

public static partial class WorkflowKeys
{
    /// <summary>How long a match may take before it stops, so a pattern that backtracks stops the match, not a thread.</summary>
    private const int TimeoutMilliseconds = 1000;

    /// <summary>A status's or transition's key: a lowercase letter, then lowercase letters, digits and dashes, 63 at most.</summary>
    [GeneratedRegex("^[a-z][a-z0-9-]{0,62}$", RegexOptions.None, TimeoutMilliseconds)]
    public static partial Regex Key();
}
