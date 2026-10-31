# Workflow definitions

A definition is JSON (camelCase), stored with every version. `WorkflowService.CreateAsync` makes a workflow from one,
and `NormalizeAsync` and `PublishAsync` check and publish drafts.

```json
{
  "format": 1,
  "statuses": [ /* WorkflowStatus */ ],
  "transitions": [ /* WorkflowTransition */ ],
  "initial": "draft",
  "anyToAny": false,
  "diagram": { }
}
```

| Field | Meaning |
|---|---|
| `format` | The stored format; written by the engine. A definition with a higher one than the engine reads is refused. |
| `statuses` | At least one. |
| `transitions` | May be empty (with `anyToAny`, every status reaches every other). |
| `initial` | The key of the status new items start in; the first status when missing. |
| `anyToAny` | Implicit transitions to every status no drawn transition reaches. Missing means false. |
| `diagram` | A designer's own layout (positions, sides), at most 256 KB. It never changes how items move. |

## Statuses

```json
{
  "number": 0,
  "key": "review",
  "names": { "en": "In review", "ar": "قيد المراجعة" },
  "category": "inProgress",
  "type": "approval",
  "settings": { "groups": ["…"], "rule": "any", "timeoutMinutes": 2880 },
  "exits": { "approved": "done", "rejected": "draft", "timedOut": "escalated" }
}
```

- `number`: 0 for a new status. The engine gives it one, and a published number never changes or comes back.
- `key`: a lowercase letter, then lowercase letters, digits and dashes, 63 at most. A published key never changes.
- `names`: by language; at least one. Names are picked by the reader's languages, then `en`, then the key.
- `category`: `todo`, `inProgress` or `done`.
- `type`: `manual` when missing. See below.
- `settings`: the type's own; checked against its schema.
- `exits`: an outcome to a status key. Every exit the type requires must be mapped, and each must lead to a status of
  this workflow.

### Built-in status types' settings

**`automated`**: runs actions, then leaves by `done`.

```json
{ "actions": [ { "type": "builtin:set-field", "settings": { "field": "priority", "value": "high" } } ] }
```

**`decision`**: routes in order. The first whose conditions hold is the exit; none holding is `otherwise`. Map each
route's exit and `otherwise`.

```json
{ "routes": [
    { "exit": "large", "when": { "match": "all", "conditions": [
        { "type": "builtin:field-value", "settings": { "field": "size", "op": "in", "values": ["L", "XL"] } } ] } },
    { "exit": "small" } ] }
```

**`wait`**: leaves by `done` after a number of minutes, or on a date field's day (UTC midnight). With a field, the
item can't enter while the field is empty.

```json
{ "minutes": 60 }
{ "field": "startDate" }
```

**`approval`**: approvers are named accounts, members of groups (read when deciding, so a membership change counts),
and the accounts in a people field of the item (one id or a list). At least one approver is needed to enter.

```jsonc
{
  "accounts": ["…"], "groups": ["…"], "field": "approvers",
  "rule": "any",          // any | all | count
  "count": 2,             // with rule "count"
  "timeoutMinutes": 2880  // or "timeoutField": "deadline" (a date field)
}
```

Exits: `approved`, `rejected`, and optionally `timedOut`. Without a `timedOut` exit, a timed-out approval leaves the
item where it is, in state `timedOut`.

## Transitions

```jsonc
{
  "number": 0,
  "key": "submit",
  "names": { "en": "Submit" },
  "from": ["draft"],
  "to": "review",
  "fields": ["body"],
  "conditions": { "match": "all", "conditions": [ /* ExtensionUse */ ], "groups": [ /* ConditionGroup */ ] },
  "checks": [ { "type": "builtin:required", "settings": { "fields": ["title"] } } ],
  "actions": [ { "type": "builtin:set-field", "settings": { "field": "reviewer", "value": "…" } } ]
}
```

- `from`: status keys, or `null` for a global transition (from every status but `to`).
- `to`: a status key; for a self loop, one of `from`.
- `fields`: what the person fills in while taking it (the transition's screen). `WorkflowItems.TakeAsync` accepts only
  these, and the item's kind checks each value (`CheckFieldAsync`).
- `conditions`: a group. `match` is `all` or `any`, and groups nest. A condition that doesn't hold hides the transition
  and refuses it, with its message.
- `checks`: every one runs; all their complaints are shown together.
- `actions`: run in order after the status is set. One that throws stops the whole transition.

An **extension use** is `{ "type": "…", "settings": { … } }`:

- `builtin:{key}`: the engine's built-ins and your host's own;
- `{plugin}:{key}`: a plugin's.

[Extensions](extensions.md) lists the built-ins and their settings.

## What's checked

Saving and publishing refuse a definition, listing every problem at once, when:

- a key is malformed or used twice;
- a published number has a new key;
- a status or transition has no name;
- a status type or extension isn't available here;
- settings don't fit the type's schema, or its own checks (a group or field that doesn't exist);
- an exit leads nowhere, or one the type requires isn't mapped;
- a transition starts or ends at a status that isn't there;
- the initial status isn't there.

Publishing also refuses to remove a status that items of any kind are in. Binding a type refuses a workflow that uses
an extension or status type not meant for that kind of item (`AppliesTo`).
