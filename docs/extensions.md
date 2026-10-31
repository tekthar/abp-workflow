# Extensions

Conditions, checks and actions are **extensions**: classes implementing `IWorkflowExtension` from
`Toda.Workflows.Contracts`. The engine's built-ins, your host's own and plugins' all go through the same contract.

| Kind | Runs | Returns |
|---|---|---|
| `condition` | Before a transition, on the item as it was; also when listing the transitions open to someone. | null when it holds, else why not. |
| `check` | While taking it, on the item as it is now, after the screen's fields. | null when it passes, else what's wrong. |
| `action` | After the status is set. Also as an `automated` status's actions. | Asks for changes with `context.SetAsync`. |

## Built-ins

Their type is `builtin:{key}`.

| Type | Kind | Settings |
|---|---|---|
| `builtin:field-value` | condition | `field`, `op` (`equals`, `notEquals`, `in`, `notIn`, `isEmpty`, `isNotEmpty`), `values` (strings), `message` |
| `builtin:person` | condition | `accounts` (ids), `groups` (ids), `who` (`reporter`, `assignee`, `projectAdmins`: read from the item's `reporter` and `assignee` values and the scope's managers) |
| `builtin:required` | check | `fields`: each must have a value |
| `builtin:changed` | check | `fields`: each must change while the transition is taken |
| `builtin:pattern` | check | `field`, `pattern` (a .NET regular expression, matched with a 100 ms limit), `message`; empty values pass |
| `builtin:set-field` | action | `field`, `value` (missing or null clears it) |

Values compare as text: a list matches if any item does. A person is matched by account id.

## Writing one

```csharp
[ExposeServices(typeof(IWorkflowExtension))]
public sealed class HasBodyCheck : IWorkflowExtension, ITransientDependency
{
    public string Key => "has-body";                             // the type is "builtin:has-body"
    public string Kind => WorkflowExtensionKinds.Check;
    public string Title => "Has a body";
    public string Description => "The document has some text in its body.";
    public string SettingsSchema => """{"type":"object","properties":{"minLength":{"type":"integer"}}}""";

    public string Summary(JsonElement? settings, string language) => "The body is filled in";

    public Task<string?> EvaluateAsync(WorkflowExtensionContext context, JsonElement? settings, CancellationToken ct)
    {
        string body = context.Values.GetValueOrDefault("body") is { ValueKind: JsonValueKind.String } b ? b.GetString()! : "";
        return Task.FromResult(body.Length >= 10 ? null : $"{context.Item.Key} needs a body first.");
    }
}
```

| Member | Purpose |
|---|---|
| `Key` | Unique among yours: lowercase letters, digits and dashes. Don't reuse a built-in's key. |
| `Kind` | One of `WorkflowExtensionKinds`. |
| `Title`, `Description` | What a designer shows. |
| `SettingsSchema` | A JSON Schema (object, properties, required, type, enum, items). Settings are checked against it on every save and import; a designer can build its form from it. |
| `EditComponent` | Optional: a designer's own edit component instead of the generated form. |
| `AppliesTo` | Optional: the kinds of item it works on. Binding a type to a workflow that uses it elsewhere is refused. |
| `Summary` | One line for the transition's list, history and traces. |
| `CheckSettingsAsync` | Problems with settings that fit the schema but not this site (a group that doesn't exist), through `IWorkflowSiteContext`. |
| `EvaluateAsync` | Conditions and checks. |
| `ExecuteAsync` | Actions. |

### What the context gives

`WorkflowExtensionContext`:

- `Item`: kind, id, key, type and scope.
- `FromStatus`, `ToStatus`, `Transition` and `ActorId` (`Guid.Empty` for the system).
- `Values`: every field by name, now, after the screen's fields.
- `Before`: the same before the transition's own fields.
- `ActorGroupIdsAsync`, `ActorManagesScopeAsync` and `ActorHoldsAssignmentsOfAsync` (delegation).
- `SetAsync(field, value)`: an action's change. The item's kind checks it, and it is saved in the same change and
  in history.
- `Feature<T>()`: something only some hosts' items offer, such as a record's children. Null where it isn't offered.
- `Log(message, level)`: a line in the tracer, never shown to the person.

`ExtensionHelpers` reads settings and values as plain strings (`Str`, `Strings`, `Texts`) and runs checks one after
another (`Problems`). Checks share the request's database context, which takes one query at a time.

### Rules

- **Throwing stops the transition** with a message naming the extension; nothing is skipped silently. Return a
  message to refuse; throw only when something is wrong.
- **An extension nobody provides** (a plugin switched off) stops transitions that use it, saying so. Saving a
  workflow that uses one is refused.
- **Keep it fast and side-effect free** in conditions: they run whenever someone lists the transitions open to them.
- **Actions don't write the item themselves.** Ask through `SetAsync`, so the kind checks the value and history
  records it. To call out (mail, a webhook), do it once the change has committed, such as through your host's events
  (`IWorkflowEvents`), not inside the transaction.
- Extensions are **transient**: leased for one use and let go.
