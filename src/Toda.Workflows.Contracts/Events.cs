namespace Toda.Workflows;

/// <summary>A transition was taken on an item (ADR 0089): which, from where to where (where it ended, past automated statuses), by whom (none: the system).</summary>
public sealed record WorkflowTransitionTaken(WorkflowItemRef Item, string Transition, string FromStatus, string ToStatus, string EndStatus, Guid? ActorId);

/// <summary>An item moved on from a waiting step (a wait's end, an approval's outcome, a plugin's step) by an exit, as the system.</summary>
public sealed record WorkflowItemAdvanced(WorkflowItemRef Item, string FromStatus, string Outcome, string EndStatus);

/// <summary>Someone decided an approval; <c>Outcome</c> is set when the decision settled it ("approved", "rejected").</summary>
public sealed record WorkflowApprovalDecided(WorkflowItemRef Item, Guid ApprovalId, string StatusKey, Guid DecidedBy, bool Approved, string? Outcome);
