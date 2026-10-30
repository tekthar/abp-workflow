using System.Diagnostics;

namespace Toda.Workflows;

/// <summary>
/// What a transition run did, collected while it runs (ADR 0034). The engine adds a step around each thing it runs;
/// extensions' log lines go to the step in progress. Written by <see cref="WorkflowTraces"/> with the record's change,
/// or on its own when the change is refused.
/// </summary>
public sealed class WorkflowTrace(
    Guid projectId, Guid recordId, string recordKey, Guid workflowId, int workflowVersion, string kind, string transition, int transitionNumber,
    string fromStatus, string toStatus, Guid? actorId)
{
    private const int MaxSteps = 500;
    private const int MaxLogLines = 100;
    private const int MaxText = 1000;

    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly List<Step> _steps = [];

    public Guid RunId { get; } = Guid.NewGuid();
    public string RecordKey { get; } = recordKey;

    /// <summary>What kind of item the run moved (ADR 0089); a record unless the runner says otherwise.</summary>
    public string ItemKind { get; init; } = Toda.Workflows.WorkflowItemKinds.Record;
    public DateTime StartedAt { get; } = DateTime.UtcNow;
    public int Hops { get; set; }

    /// <summary>Set once the run is in the database, so a failure afterwards doesn't write it twice.</summary>
    public bool Saved { get; set; }

    /// <summary>How many steps it has (a resumed plugin step's run is kept only when the step said something).</summary>
    public int StepCount => _steps.Count;

    /// <summary>The step in progress, which an extension's log lines belong to.</summary>
    public Step? Current { get; private set; }

    public Step Begin(string phase, string type, string? label = null, string? statusKey = null, string? path = null)
    {
        Step step = new(phase, type, Clip(label, 500), statusKey, path);
        if (_steps.Count < MaxSteps)
        {
            _steps.Add(step);
        }

        Current = step;
        return step;
    }

    public void End(Step step, string outcome, string? message = null)
    {
        step.Outcome = outcome;
        step.Message = Clip(message, 2000);
        step.DurationMs = (int)step.Clock.ElapsedMilliseconds;
        Current = null;
    }

    /// <summary>A step that takes no time: an entry, an exit.</summary>
    public void Note(string phase, string type, string outcome, string? label = null, string? statusKey = null, string? message = null, Guid? activityId = null)
    {
        Step step = Begin(phase, type, label, statusKey);
        step.ActivityId = activityId;
        End(step, outcome, message);
    }

    public void Log(string level, string message)
    {
        if (Current is { } step && step.Logs.Count < MaxLogLines)
        {
            step.Logs.Add(new WorkflowRunLog(level is "warn" or "error" ? level : "info", Clip(message, MaxText)!));
        }
    }

    /// <summary>The rows to write: the run with its outcome, and its steps.</summary>
    public (WorkflowRun Run, List<WorkflowRunStep> Steps) ToRows(string outcome, string? message, string? endStatus, Guid? changeSetId)
    {
        // A step still open when the run stopped is where it stopped.
        if (Current is { } open)
        {
            End(open, WorkflowRunStepOutcomes.Error, message);
        }

        WorkflowRun run = new()
        {
            Id = RunId,
            ScopeId = projectId,
            ItemId = recordId,
            ItemKind = ItemKind,
            ItemKey = RecordKey,
            WorkflowId = workflowId,
            WorkflowVersion = workflowVersion,
            Kind = kind,
            Transition = transition,
            TransitionNumber = transitionNumber,
            FromStatus = fromStatus,
            ToStatus = toStatus,
            EndStatus = endStatus,
            ActorId = actorId,
            Outcome = outcome,
            Message = Clip(message, 2000),
            ChangeSetId = changeSetId,
            StartedAt = StartedAt,
            DurationMs = (int)_clock.ElapsedMilliseconds,
            Hops = Hops,
        };
        List<WorkflowRunStep> steps = _steps.Select((s, i) => new WorkflowRunStep
        {
            Id = Guid.NewGuid(),
            RunId = RunId,
            At = StartedAt,
            Seq = i + 1,
            Phase = s.Phase,
            StatusKey = s.StatusKey,
            Type = s.Type,
            Label = s.Label,
            Path = s.Path,
            Outcome = s.Outcome ?? WorkflowRunStepOutcomes.Error,
            Message = s.Message,
            DurationMs = s.DurationMs,
            Logs = [.. s.Logs],
            ActivityId = s.ActivityId,
        }).ToList();
        return (run, steps);
    }

    private static string? Clip(string? text, int max) => text is null || text.Length <= max ? text : text[..(max - 1)] + "…";

    public sealed class Step(string phase, string type, string? label, string? statusKey, string? path)
    {
        internal Stopwatch Clock { get; } = Stopwatch.StartNew();
        public string Phase { get; } = phase;
        public string Type { get; } = type;
        public string? Label { get; } = label;
        public string? StatusKey { get; } = statusKey;
        public string? Path { get; } = path;
        public string? Outcome { get; set; }
        public string? Message { get; set; }
        public int DurationMs { get; set; }
        public Guid? ActivityId { get; set; }
        public List<WorkflowRunLog> Logs { get; } = [];
    }
}

/// <summary>
/// The workflow itself is broken: a missing extension or exit, a loop of automated statuses, an extension that threw.
/// The person sees the message like any refusal; the tracer records the run as failed, not refused (ADR 0034).
/// </summary>
public sealed class WorkflowFailureException(string message) : Volo.Abp.UserFriendlyException(message);
