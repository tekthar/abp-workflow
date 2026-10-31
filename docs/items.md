# Items

The engine moves your items without knowing what they are. There are two ways to give it items:

- **`IWorkflowItemKind`** (in `Toda.Workflows.Contracts`): one class answers for a kind of item; the engine does
  everything else through `WorkflowItems`. Start here.
- **`IWorkflowItemStore` with a `WorkflowItemSession`** (in `Toda.Workflows`): you open the item yourself and drive the
  runner's phases, so your own edits sit between them. For items with more to them than a status and values, such as
  children, links, history of their own, or an edit form that also takes transitions. Toda's records and assets work
  this way.

## IWorkflowItemKind

Register it in DI under the interface (`[ExposeServices(typeof(IWorkflowItemKind))]`). A plugin host leases it from a
plugin instead (see [Hosting](hosting.md)). Your host's own kind is named by its `Key` ("document"); a plugin's is
`{plugin}:{key}`.

| Member | What to give |
|---|---|
| `Key` | `^[a-z][a-z0-9-]{0,31}$`. |
| `Noun` | One item in a sentence: "document" ("3 documents are in 'review'"). |
| `Title` | The list's name where an admin binds them: "Documents". |
| `Scope` | `WorkflowItemScopes.Site`, or `Project` when items live in scopes (the snapshot's scope id is theirs). |
| `Types` | At least one `WorkflowItemType(key, title)`: each binds to a workflow of its own. |
| `Fields` | Values by name and shape (`text`, `number`, `date`, `person`, `people`, `choice`), so settings that read one can be checked. |
| `OpenAsync(id)` | The item now, as a `WorkflowItemSnapshot`; null when it's gone. |
| `RightsAsync(item, account)` | `WorkflowItemRights(Read, Move, ManagesScope, WhyNot)`. Never asked when the system acts. |
| `CheckFieldAsync(item, field, value)` | The value to store for a field a workflow sets; throw `WorkflowItemException` to refuse it. The default refuses every field. |
| `SaveAsync(change)` | Write the status and fields. Check `change.Version` against yours and bump it; a stale one is `new WorkflowItemException("…", conflict: true)`. |
| `InStatusesAsync(selection, statuses)` | How many items of the selection sit in each status; all statuses when null. |
| `MoveStatusesAsync(selection, map, changeSetId, actor)` | Rebinding: move the selection's items from old statuses to new ones. |
| `PublishedAsync(selection, version, statuses)` | Optional: a new version was published, for a kind that keeps a copy of the statuses. |

**Every call runs in the change that moves the item.** Use `IDbContextProvider<YourContext>`, which hands you the
same context, in the same transaction, as the engine's. Don't commit, open your own transaction, or keep anything
between calls.

### Values

A snapshot's values are the item's fields by name, as JSON. Conditions, checks, approvals and waits read them:

| Shape | Stored as |
|---|---|
| text, choice | a string |
| number | a number |
| date | `"yyyy-MM-dd"` |
| person | the account id as a string |
| people | a list of account ids |
| empty | `null`, or leave it out |

`status` and `type` are the engine's names: don't use them as field names. An item's key and type are at most 64
characters each.

### The snapshot

```csharp
new WorkflowItemSnapshot(
    new WorkflowItemRef(Kind: "document", Id: d.Id, Key: d.Key, Type: d.Type, ScopeId: null, ScopeKey: null),
    Status: d.Status, Version: d.Version, Values: values);
```

The engine copies what you return and overwrites the kind with the name it knows you by.

## WorkflowItems

The engine's service for items of any `IWorkflowItemKind`. Each method that changes something runs in a unit of work
of its own, as `requiresNew`, and traces what it did.

| Method | Does |
|---|---|
| `BoundAsync(kind, type, scopeId)` | The workflow a type follows there (the scope's, else the site's), or null. |
| `StartAsync(actor, kind, id)` | A new item enters its first status: an approval asks, a wait starts. Save the item in `bound.Definition.InitialStatus` first. |
| `GetAsync(actor, kind, id)` | `WorkflowItemState`: status, version, workflow, the step it waits in; null when the actor can't read it. |
| `TransitionsAsync(actor, kind, id)` | The transitions the actor may take now (their conditions hold), with their targets. |
| `TakeAsync(actor, kind, id, transition, version?, fields?)` | Takes one, by key or number. |
| `ApprovalsAsync(actor, kind, id)` | The approvals, newest first, with decisions and whether the actor may decide each now. |
| `DecideAsync(actor, kind, id, approvalId, approve, comment?)` | Decides; once an approval is settled, the item moves on through its exit. |
| `RunsAsync(actor, kind, id, movedOnly)` | Its runs, newest first, at most 200. |
| `KindsAsync(scopeId)` | Every kind and its bindings, at the site or in a scope (with what it inherits). |
| `BindAsync(actor, kind, type, scopeId, workflow, map?, apply?, changeSetId?)` | Binds, rebinds or unbinds (`workflow` null). With `apply` false, returns what would be stranded and changes nothing. |

The actor is an account id, or `Guid.Empty` for the system (no rights asked). **Who may bind is yours to check**
before calling `BindAsync`.

A kind that isn't there now (a plugin switched off) is a conflict that says so. Its items wait where they are, and
the worker moves them on once it's back.

## A store of your own

When an item needs more than the contract, implement `IWorkflowItemStore` (registered under that interface, with its
`Kind`) and a `WorkflowItemSession` subclass:

- the session reads and writes your entity directly: `Status`, `ValuesAsync`, `BeforeAsync`, `SetStatus`,
  `SetFieldAsync`, `UnlessMayChangeStatus`, the actor's groups and scope rights;
- it offers features only your items have, through `Feature<T>()`, for extensions that ask for them;
- the store opens an item as the system for the worker, saves it, counts items per status and follows publishes.

Then take a transition in your own change, with your edits between the phases:

```csharp
TransitionRun run = runner.Begin(session, bound, transitionKey, targetStatus: null, typeName)!;
await runner.CheckAsync(run, ct);      // conditions, on the item as it was
// ... apply the request's own edits ...
await runner.CompleteAsync(run, ct);   // checks on the item as it is now, then the status
// ... work that only happens on the way to done ...
await runner.FinishAsync(run, ct);     // actions, then entering the target
// ... save the item and the engine's rows, in this change ...
```

Wrap it in `WorkflowTraces.GuardAsync`, as `WorkflowItems.TakeAsync` does, so a refused run is still written.
