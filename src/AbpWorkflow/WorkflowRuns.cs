
namespace AbpWorkflow;

/// <summary>
/// One transition attempt on a record, as the workflow tracer shows it: which transition, who, where the
/// record was and where it ended, how it came out, and the change set it wrote. Kept even when the attempt was refused.
/// </summary>
public class WorkflowRun : IWorkflowActed
{
    public Guid Id { get; set; }
    /// <summary>Where the item lives (a record's project, an asset's schema), as its kind says.</summary>
    public Guid ScopeId { get; set; }

    public Guid ItemId { get; set; }

    /// <summary>What kind of item ran (<see cref="AbpWorkflow.WorkflowItemKinds"/>); the id above is its id.</summary>
    public string ItemKind { get; set; } = AbpWorkflow.WorkflowItemKinds.Record;

    /// <summary>The item's key at the time (a record's can change with its type).</summary>
    public string ItemKey { get; set; } = null!;

    public Guid WorkflowId { get; set; }
    public int WorkflowVersion { get; set; }

    /// <summary>One of <see cref="WorkflowRunKinds"/>.</summary>
    public string Kind { get; set; } = WorkflowRunKinds.Transition;

    /// <summary>The transition's key ("to:{status}" for an implicit any-to-any one); for an exit run, the outcome.</summary>
    public string Transition { get; set; } = null!;

    public int TransitionNumber { get; set; }
    public string FromStatus { get; set; } = null!;

    /// <summary>The status the transition leads to.</summary>
    public string ToStatus { get; set; } = null!;

    /// <summary>Where the record ended: past automated and decision statuses. Null when it didn't move.</summary>
    public string? EndStatus { get; set; }

    /// <summary>Null when the system ran it (an exit after a wait or an approval).</summary>
    public Guid? ActorId { get; set; }

    /// <summary>Who acted for the account above, when that was someone else.</summary>
    public Guid? ActingAccountId { get; set; }

    /// <summary>How they acted for them: impersonation or delegation.</summary>
    public string? ActingMode { get; set; }

    /// <summary>One of <see cref="WorkflowRunOutcomes"/>.</summary>
    public string Outcome { get; set; } = WorkflowRunOutcomes.Moved;

    /// <summary>Why it was refused, or the error.</summary>
    public string? Message { get; set; }

    /// <summary>The record history it wrote (<see cref="RecordChange.ChangeSetId"/>); null when refused.</summary>
    public Guid? ChangeSetId { get; set; }

    public DateTime StartedAt { get; set; }
    public int DurationMs { get; set; }

    /// <summary>How many statuses it passed through after the first (automated and decision statuses).</summary>
    public int Hops { get; set; }
}

public static class WorkflowRunKinds
{
    /// <summary>A transition taken by a person (or by PATCH status, which runs the one transition there).</summary>
    public const string Transition = "transition";

    /// <summary>The system moving a record on from a wait or an approval through an outcome's exit.</summary>
    public const string Exit = "exit";

    /// <summary>A plugin's step at work when its time came or a signal arrived (an AI step's run): its log, without a move.</summary>
    public const string Step = "step";
}

public static class WorkflowRunOutcomes
{
    public const string Moved = "moved";

    /// <summary>A condition, check, required field or permission said no.</summary>
    public const string Refused = "refused";

    /// <summary>Something broke: a missing extension, a loop of automated statuses, an error.</summary>
    public const string Failed = "failed";

    /// <summary>A step did its work in place (<see cref="WorkflowRunKinds.Step"/>): it left by an exit, or keeps waiting.</summary>
    public const string Worked = "worked";
}

/// <summary>One thing that ran inside a <see cref="WorkflowRun"/>, in order.</summary>
public class WorkflowRunStep
{
    public Guid Id { get; set; }
    public Guid RunId { get; set; }

    /// <summary>The run's start, repeated so old steps can be pruned without a join.</summary>
    public DateTime At { get; set; }

    public int Seq { get; set; }

    /// <summary>One of <see cref="WorkflowRunPhases"/>.</summary>
    public string Phase { get; set; } = null!;

    /// <summary>The status the step belongs to (entries, actions, routes, exits); null for the transition's own steps.</summary>
    public string? StatusKey { get; set; }

    /// <summary>The extension type ("builtin:assign"), the status type, or "group" for an all/any group.</summary>
    public string Type { get; set; } = null!;

    /// <summary>What the step was: the extension's one-line summary, a route's exit, the outcome taken.</summary>
    public string? Label { get; set; }

    /// <summary>Where a condition sits in its tree: "1", "2.1" (the first condition of the second group).</summary>
    public string? Path { get; set; }

    /// <summary>One of <see cref="WorkflowRunStepOutcomes"/>.</summary>
    public string Outcome { get; set; } = null!;

    public string? Message { get; set; }
    public int DurationMs { get; set; }
    public List<WorkflowRunLog> Logs { get; set; } = [];

    /// <summary>The wait or approval the entry started, when it did.</summary>
    public Guid? ActivityId { get; set; }
}

/// <summary>A line an extension logged while it ran.</summary>
public sealed record WorkflowRunLog(string Level, string Message);

public static class WorkflowRunPhases
{
    public const string Condition = "condition";
    public const string Check = "check";

    /// <summary>The record entering a status.</summary>
    public const string Enter = "enter";

    /// <summary>An action: a transition's (no status key) or an automated status's (its key).</summary>
    public const string Action = "action";

    /// <summary>One of a decision's routes, tried in order.</summary>
    public const string Route = "route";

    /// <summary>The record leaving a status through an outcome's exit.</summary>
    public const string Exit = "exit";
}

public static class WorkflowRunStepOutcomes
{
    public const string Passed = "passed";
    public const string Failed = "failed";
    public const string Done = "done";
    public const string Error = "error";
}
