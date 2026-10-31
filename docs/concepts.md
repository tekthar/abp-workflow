# Concepts

## Workflows, drafts and versions

A **workflow** (`Workflow`) has a key unique in its scope, names by language, a description, and a **definition**:
its statuses and transitions. A workflow belongs to the site (`ScopeId` null) or to one scope, such as a project or a
department: whatever your host calls a scope.

The definition items move by is the **published** one. Editing happens on a **draft** (`Workflow.Draft`). Publishing
checks the draft again and makes it the next **version**. Every version is kept (`WorkflowVersion`): a step that started
in version 3 finishes by version 3's exits, even after version 4 is published.

Status and transition **keys and numbers never change once published**. Names, categories, settings and everything
else may. Publishing refuses to remove a status that items are in: move them first.

The stored definition carries a **format** number. A definition from a newer engine is refused with a message saying so.

## Statuses and categories

A **status** has a key, names by language and a **category**:

- `todo` (to do);
- `inProgress` (being worked on);
- `done`.

Lists, boards and reports across workflows group by category, not by status.

A status also has a **type**, which says what happens when an item comes in:

| Type | What it does | Exits it must map |
|---|---|---|
| `manual` (default) | Nothing: waits for a person to take a transition. | none |
| `automated` | Runs its actions, then leaves at once. | `done` |
| `decision` | Checks its routes' conditions in order, leaves by the first that holds. | each route's exit, and `otherwise` |
| `wait` | Waits a number of minutes, or until a date field's day. | `done` |
| `approval` | Waits for approvers' decisions. | `approved`, `rejected`; `timedOut` may be mapped |
| `builtin:{key}` | A status type of your host's own. | its exits |
| `{plugin}:{key}` | A plugin's status type. | its exits |

A status's **exits** map an outcome to the status the item goes to next. Statuses that leave at once can chain.
`automated` and `decision` statuses and sync step types can pass an item along, at most 20 in one move. A loop is
stopped and the move refused.

## Transitions

A **transition** has a key, names, the statuses it starts **from** and the one it goes **to**:

- **Drawn transitions** start from a list of statuses.
- **A global transition** (`from: null`) starts from every status but its own target.
- **Any-to-any** (`anyToAny: true` on the definition) adds an implicit transition (`to:{status}`) to every status that
  no drawn transition reaches. Implicit transitions have no extensions. With any-to-any off, only drawn transitions
  exist.
- **A self loop** (`to` equals a `from`) changes nothing but runs its checks and actions.

Taking a transition runs in this order, all in one change:

1. **Conditions**, on the item as it was. They decide whether the person sees the transition at all, and refuse it if
   they don't hold.
2. **The screen's fields** (`fields`): values the person fills in as they take it.
3. **Checks**, on the item as it is now. Every complaint is collected and shown together.
4. **The status** is set (the right to change it is asked again).
5. **Actions** change the item.
6. **The target status is entered:**
   - an `automated` or `decision` status, or a sync step type, moves the item on at once;
   - a `wait`, an `approval` or an async step starts.

If anything fails, nothing of it is saved, but the run is still traced as refused or failed.

## Items, kinds, types and scopes

An **item** is anything a workflow moves: a document, an order, a record. The engine never reads your entity. It sees
a `WorkflowItemRef` (kind, id, key, type, scope) and the item's values by name.

- **Kind:** what sort of thing it is ("document", "order"). A host's own kind is named by its key. A plugin's is
  `{plugin}:{key}`.
- **Type:** a kind's subtype that binds to a workflow ("memo", "contract"). Each type can follow a different workflow.
- **Scope:** where an item lives (a project), or none. A kind's items are either the site's (`WorkflowItemScopes.Site`)
  or live in scopes (`WorkflowItemScopes.Project`).

A **binding** (`WorkflowBinding`) says which workflow a kind's type follows, at the site or in one scope. A scope's
own binding wins over the site's. Binding a workflow that lacks statuses some items are in needs a map from each old
status to a new one; the items move in the same change.

## Runs and the tracer

Every transition taken or refused, and every exit an item leaves a waiting step by, is a **run** (`WorkflowRun`). A
run has its outcome:

- `moved`: the transition was taken;
- `refused`: a condition, check or rule said no;
- `failed`: an extension threw.

It also keeps the statuses it went from, to and ended in, who acted, and how long it took. Its **steps**
(`WorkflowRunStep`) are each condition, check, action, entry, route and exit, with their outcome, message
and the lines extensions logged.

Steps are kept 30 days and runs 365 by default (`Workflows:Trace:StepDays`, `Workflows:Trace:RunDays`).

## Activities: steps that wait

An item that enters a `wait`, an `approval` or an async step type gets an **activity** (`WorkflowActivity`): the
visit, with its state.

| State | Meaning |
|---|---|
| `pending` | Waiting: for a time, approvers or a signal. |
| `completed` | Came to an outcome; the item is owed the move by that exit. |
| `advanced` | The item moved on. |
| `timedOut` | An approval timed out and its status has no `timedOut` exit: the item stays, and says so. |
| `abandoned` | The item left the status some other way first. |
| `failed` | Moving the item on failed five times; `LastError` says why. |

**Approvals** keep each person's **decision** (`ApprovalDecision`). One rejection rejects. Enough approvals approve:
one for `any`, every approver for `all`, or `count` of them. Managers of the item's scope may decide for the approvers,
marked as such. One person decides once.

## The worker

`WorkflowWorker` runs in every server, every two seconds. It:

- picks up activities whose time has come;
- ends waits;
- times out approvals;
- resumes async steps whose time came;
- moves on items whose step completed.

Each activity is claimed with a conditional update, so servers never work the same one twice. A move that fails is
retried with back-off: 1, 2, 4 and 8 minutes. After five tries the activity is `failed`.

Signals (`IWorkflowSignals`) resume a waiting step from outside: a reply came, an outside system answered. A step is
resumed under a row lock, so a signal and the timer never both run it.
