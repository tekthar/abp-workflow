# Troubleshooting

Messages you may meet, and what to do. Most are written for the person taking a transition; some are for you.

## Saving or publishing a workflow

**"Status 'x' has type 'builtin:y', which isn't available here."**
Nobody registered that status type. Register your `IWorkflowStatusType` under that interface
(`[ExposeServices(typeof(IWorkflowStatusType))]`), or check that the plugin is on. Its mode must be `sync` or
`async`, or it's skipped with a warning in the log.

**"The workflow uses 'builtin:y', which isn't available here."**
The same for an extension: register it under `IWorkflowExtension`.

**"Status 'review' needs a status to go to when 'approved'."**
Map every exit the status's type requires in `exits`. A decision also needs each route's exit and `otherwise`.

**"Status 3 is 'x'; a published status keeps its key. Give 'y' number 0 to add it."**
You renamed a published status. Keys and numbers are permanent. Keep the key and change the name, or add a new status
with number 0.

**"N documents are in 'draft', so it can't be removed yet."**
Publishing refuses to drop a status items sit in. Move them first, or keep the status.

**"There's no field 'x'." / an approval or wait refused for its field**
The settings' check found no such field, or not the right shape. A wait's field must be a date; an approval's field
must be a person or people. Declare the field in your kind's `Fields`, or give your own `IWorkflowSiteContexts`.

## Binding

**"'kind' isn't available now: its items can't be bound until it is."**
No `IWorkflowItemKind` with that key is registered, or its plugin is off.

**"Map each status the workflow lacks to one of its statuses: 'draft' has 3 document(s)."**
Pass a map from each old status to one of the new workflow's. Call with `apply: false` first to see the counts.

**"X can't move documents: …"**
The workflow uses an extension or status type whose `AppliesTo` leaves your kind out.

## Taking a transition

**"'approve' can't be taken from Draft. From here: …"**
The transition doesn't start at the item's status. The message lists the ones that do.

**"document has no transition 'x'."** (the kind's noun)
No such key or number in the workflow.

**"Only … can do this." (403)**
A condition refused. `TransitionsAsync` lists only the transitions whose conditions hold for the actor.

**"DOC-1 was changed by someone else since you opened it." (409)**
The version you sent is stale. Read the item again.

**"This document follows no workflow: its type isn't bound to one here."**
Bind the type, at the site or in the item's scope.

**"'submit' doesn't ask for body."**
Only the transition's `fields` can be sent with it.

**"The workflow passed through 20 statuses without stopping (last: x)."**
Automated or decision statuses, or sync step types, form a loop. Give one of them a way out to a manual status.

**"Nobody can approve DOC-1 in 'review': its approvers' field is empty."**
The approval reads a people field that's empty. Fill it in before the transition (a check `builtin:required` on
the transition into it helps).

## Approvals

**"You aren't one of the approvers of DOC-1." (403)**
The actor isn't named, isn't in an approver group (ask `IWorkflowPeople`), and doesn't manage the item's scope.

**"You've already decided on this approval." / "This approval of DOC-1 is over (completed)." (409)**
One person decides once; a settled approval can't be decided again.

## Waiting steps

**An item stays in an approval after its timeout.**
Its status has no `timedOut` exit. The activity is `timedOut`; map the exit to move it on.

**An activity is `failed`.**
Moving the item on failed five times. Its `LastError` says why. Fix the cause, such as an action that throws or
a status removed from the workflow, then set the activity back to `completed` with `NextSweepAt` now. The worker tries
again.

**Nothing moves at all.**
Check that the host runs its hosted services (the worker starts with the web host), and the log for "The workflow
sweep failed".
