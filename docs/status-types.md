# Status types

A status's type says what happens when an item comes in. Besides the built-ins (`manual`, `automated`, `decision`,
`wait`, `approval`; see [Workflow definitions](definitions.md)), your host and plugins can add their own with
`IWorkflowStatusType` from `Toda.Workflows.Contracts`.

- **Sync** (`StatusTypeModes.Sync`): does its work as the item comes in, inside the change that moved it, then leaves
  by an exit at once. For example: notify someone, compute a value, route by an outside answer.
- **Async** (`StatusTypeModes.Async`): plans its work and waits, for a time, a signal, or both. The worker calls it
  back when the time comes, and a signal through `IWorkflowSignals` resumes it. It then leaves by an exit or keeps
  waiting. For example: wait for a reply, a payment or a countersignature.

A type never writes the item. It says which exit to leave by; the engine does the rest: the activity, leases, retries,
the hop limit and moving the item.

## A sync type

```csharp
[ExposeServices(typeof(IWorkflowStatusType))]
public sealed class NotifyStatusType(SampleOutbox outbox) : IWorkflowStatusType, ITransientDependency
{
    public string Key => "notify";                  // the type is "builtin:notify"
    public string Mode => StatusTypeModes.Sync;
    public IReadOnlyList<string> Exits => ["done"];
    public string Title => "Notify";
    public string Description => "Sends a message about the item, then moves on.";
    public string SettingsSchema => """{"type":"object","required":["message"],"properties":{"message":{"type":"string"}}}""";

    public Task<StatusEntryResult> EnterAsync(WorkflowStatusContext context, JsonElement? settings, CancellationToken ct)
    {
        outbox.Sent.Enqueue($"{context.Item.Key}: {settings?.GetProperty("message").GetString()}");
        context.Log("Sent");
        return Task.FromResult(StatusEntryResult.Leave("done"));
    }
}
```

## An async type

```csharp
public sealed class CountersignStatusType : IWorkflowStatusType, ITransientDependency
{
    public string Key => "countersign";
    public string Mode => StatusTypeModes.Async;
    public IReadOnlyList<string> Exits => ["signed"];
    // Title, Description, SettingsSchema ...

    public Task<StatusEntryResult> EnterAsync(WorkflowStatusContext context, JsonElement? settings, CancellationToken ct) =>
        Task.FromResult(StatusEntryResult.Wait(until: null));   // only a signal ends it

    public Task<StatusResumeResult> ResumeAsync(WorkflowStatusContext context, JsonElement? settings, JsonElement? signal, CancellationToken ct) =>
        Task.FromResult(signal?.TryGetProperty("by", out _) == true
            ? StatusResumeResult.Leave("signed")
            : StatusResumeResult.KeepWaiting(until: null));
}
```

Signal it from your own endpoint, a webhook or a queue consumer:

```csharp
PendingStep step = (await signals.PendingAsync(itemId)).First(s => s.StatusType == "builtin:countersign");
await signals.SignalAsync(step.ActivityId, JsonSerializer.SerializeToElement(new { by = accountId }));
```

`SignalAsync` returns false when the step isn't waiting any more. A step that comes to an exit moves the item at once.
If that fails, the worker tries again.

## The contract

| Member | Purpose |
|---|---|
| `Key`, `Mode`, `Title`, `Description` | As for extensions. Your host's type is `builtin:{key}`; a plugin's is `{plugin}:{key}`. |
| `Exits` | Exits every status of this type must map. |
| `OptionalExits` | Exits it may map. One that's taken but not mapped leaves the item where it is, saying so. |
| `RoutesSetting` | The settings property that names more exits (a list of names, or of objects with an `exit`), each of which must be mapped, as a decision's routes are. |
| `AppliesTo` | The kinds of item it works on; null for any. |
| `SettingsSchema`, `CheckSettingsAsync`, `Summary`, `EditComponent` | As for extensions. |
| `Panel` | A component an app shows with a step waiting in a status of this type, given the item's key, the activity id and the status. |
| `EnterAsync` | The item came in: `StatusEntryResult.Leave(exit)`, or `Wait(until, state)`. |
| `ResumeAsync` | The time came (`signal` null) or a signal arrived: `Leave(exit)`, or `KeepWaiting(until, state)`. |

`WorkflowStatusContext` gives:

- the item, its status key and values;
- the activity id once it waits;
- the `State` it kept when it began waiting;
- `ValuesAsAsync(account)`: the values as an account may read them;
- `Feature<T>()` and `Log`.

Keep state small: it's stored as JSON on the activity.

## When the type isn't there

A status whose type nobody provides refuses new entries with a message. Items already waiting in it stay. The worker
looks at them again every five minutes, and at once when the host's extensions change (a plugin is switched on).
