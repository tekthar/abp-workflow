# 0089. Workflows as a package

- **Status:** Accepted (the user chose the staged order and approved this record on 29 Oct 2026, with assets getting
  workflows of their own right after the seam, and the marketplace waiting for the package)
- **Date:** 2026-10-29
- **Revisit at:** toda-workflow 1.0, or a fourth kind of item besides records, assets and the marketplace's

## Context

Toda Marketplace needs workflows to publish apps. Publisher registration, new listings, new versions and AI review
are each a process: a few statuses, approvals, a step an AI agent takes, and a person who signs off. Building a
second engine there would mean two to keep right. Guard showed the way (ADR 0087): find what's here, put it behind
contracts, and publish it as a package both products use.

Most of the engine is already neutral about what moves:

- **Definitions:** statuses, transitions, conditions, checks and actions, as JSON in `projects.workflows`
  and `projects.workflow_versions` (ADR 0018, 0019).
- **Which transitions are open:** `WorkflowEngine.Available` and `Resolve` read only the definition.
- **Validation and publishing:** `WorkflowService.NormalizeAsync`, `WorkflowSchema` and the publish checks.
- **Status types and extensions:** one contract each for built-ins and plugins (ADR 0022), with the worker,
  signals, the 20-hop guard and the tracer (ADR 0034).

What stands in the way:

- **The transition pipeline is inside `RecordService.UpdateAsync`.** Lines 521 to 773 of the 1,700-line `RecordService.cs`
  resolve the transition, check conditions, apply the transition's fields between other edits, run checks, set
  the status, run actions and enter the target. They share local state with field edits, scheduling, sprints,
  links, unfinished work, resolution and the layout checks. `AdvanceAsync` and `DecideAsync` repeat part of it for
  the worker and approvals.
- **The contexts are records.** `TransitionContext` holds a `Record`, a `Project`, a `RecordType` and the
  `ProjectsDbContext`. `StatusEntry` reads `record.DueDate` and `record.Fields`. `WorkflowActivity` and
  `ApprovalDecision` have a `RecordId`. The plugin-facing `WorkflowExtensionContext` and `WorkflowStatusContext`
  speak of `RecordKey`, `ProjectKey`, children, links and mailing customers.
- **The tables live in the `projects` schema**, beside records.
- **Rights are Casl abilities on a record.** The right to change the status is `ability.UnlessCan("update", record,
  "status")`.

There are three users to design for, not one:

1. **Records**, as today.
2. **Assets** (ADR 0079, 0080). A schema's statuses have categories and are set freely, with no transitions. The
   natural lifecycle is retiring or disposing of an asset with an approval.
3. **A marketplace listing**, and a publisher's application, in another product with its own models, its own
   rights through Guard and no undo.

## Decision

**The engine owns how an item moves. The host owns what the item is.** Workflows become a set of versioned
packages, `Toda.Workflows*`, that Toda and Toda Marketplace both consume. The staged order is the user's (29 Oct):
first a seam inside this repository for records only, then assets as a second kind, then the package.

### What is in the package

- **Definitions:** `Workflow`, `WorkflowVersion`, `WorkflowDefinition` with its statuses, transitions, extension uses
  and condition groups, drafts, published versions and the numbers that are never reused.
- **Checking and publishing:** normalising, the settings schemas, the publish problems.
- **The engine:** which transitions are open, which one a request means, implicit any-to-any moves.
- **The runner** (`WorkflowRunner`): taking a transition, entering and leaving statuses, the hop guard, advancing
  from a waiting step. It is the pipeline lifted out of `RecordService`, described below.
- **Status types:** manual, automated, decision, wait and approval, and the contract for plugins' types.
- **Activities and approvals:** waiting visits (the outbox), approval rules, decisions with delegation.
- **The worker and signals:** the sweeper, retries, leases, resuming a waiting step.
- **The tracer:** runs and their steps (ADR 0034).
- **Extensions that only read or write fields:** `field-value`, `person`, `required`, `changed`, `pattern`,
  `set-field`.

### What stays in Toda

A new `src/Toda.Workflows.Integration` (renamed with the app) holds Toda's side:

- the record item store over `RecordService`, and the asset item store over the asset service (stage 2);
- Casl and Guard behind the actor seam, and delegation (ADR 0042);
- change sets and undo (ADR 0085): the store's writes join the host's change set;
- `WorkflowSync` and the binding of record types (`record_types.workflow_id`), and the type's status copy;
- the record-only extensions: `children-done`, `linked-status`, `assign` and `assign-to-group`;
- scripts (`ScriptExtensions`), notices, the controllers and their routes;
- the AI step and agent steps, as status types the host registers;
- the plugin bridge: plugin leases for extensions and status types.

### The item contract

An item is anything that has a status, a version and fields: a record, an asset, a listing. The runner never sees
its class. It sees a reference and asks the host's store for the rest.

```csharp
// Identity: the kind picks the store; the scope is a project, a schema, a marketplace, or none.
public sealed record WorkflowItemRef(string Kind, Guid Id, string Key, string Type, Guid? ScopeId, string? ScopeKey);

// The status as stored, with its category, and the version that guards against a lost update.
public sealed record WorkflowItemState(WorkflowItemRef Item, string Status, StatusCategory Category, long Version);
```

Every seam has a `TryAdd` default, as Guard's do (ADR 0087). Toda replaces them in its integration module.

| Seam | What it answers | Default | Toda's |
|---|---|---|---|
| `IWorkflowItemStore` (one per kind) | load and lock an item; its workflow (the binding); set its status; apply a change inside the host's transaction; history | none: a host must give one | records over `RecordService`; assets (stage 2) |
| `IWorkflowFieldAccess` (from the store) | an item's values by name as stored JSON, before and now; set one; a field's kind, for checks | none | records adapt the field engine; assets their schema's attributes |
| `IWorkflowActors` | may this actor change this item's status; their groups; do they manage the scope; whose approvals or assignments they hold as a delegate | everyone may; no groups; no delegation | Casl, Guard's membership, delegations |
| `IWorkflowEvents` | transition taken, status entered, step waiting, approval decided, step failed | nothing | `Toda.Sdk.IEventPublisher` in the change's unit of work |
| `IWorkflowExtensionSource` | the extensions and status types there are | DI | plugin leases (ADR 0022) |
| `IWorkflowSiteContext` | for checks: groups, accounts, service accounts, field kinds | DI lookups | today's `SiteContext` |
| `IWorkflowScopes` | which workflows a scope may use (its own and the site's) | site scope only | projects; asset schemas |
| `IWorkflowLocks` | the worker's leases | Postgres advisory locks | cluster locks |

Rules that hold for every store:

- **The host owns the unit of work.** The runner never commits. It writes its own rows (activities, decisions,
  traces) through `WorkflowsDbContext` in the same connection and transaction, as ABP does for two contexts on one
  connection string. The sample host proves it with a crash between the writes.
- **The store writes history.** A status or field the runner sets is the store's change, recorded where the item
  keeps its history (`record_changes`, the asset's history) in the host's change set. Undo stays the host's: it
  moves an item back through the runner like a person would, as `UndoService.TransitionBackAsync` does now.
- **Rights are asked, never assumed.** The runner asks `IWorkflowActors` before taking a transition. An action's
  change isn't checked against the person's field rights, as today: the workflow's author decided it. The system
  (the worker) acts with full rights, as `StatusEntry.SystemAbility` does now.
- **The actor is opaque.** `WorkflowActor(Guid Id, object? Principal)` carries the host's own principal (Toda's
  `Ability`) back to the host's seams. The package never reads it.

### Extensions see items, not records

`IWorkflowExtension`, `IWorkflowStatusType`, `IWorkflowSignals` and their contexts move to
`Toda.Workflows.Contracts`, namespace `Toda.Workflows`. The contexts become item-shaped:

- `Item` (`WorkflowItemRef`) in place of `RecordKey`, `RecordType` and `ProjectKey`; `FromStatus`, `ToStatus`,
  `Transition`, `ActorId`, `Values`, `Before`, `SetAsync` and `Log` as today.
- **Host features are asked for by type:** `context.Feature<T>()` returns null where the host has none. Toda offers
  `IRelatedRecords` (children and linked records), `ICustomerMail` (`MailCustomersAsync`), `IValuesAs` (an
  account's view of the values, ADR 0086) and `IProjectRoles` (the project lead).
- **An extension says which kinds it applies to:** `AppliesTo`, null for any. `children-done` says `record`, so the
  designer doesn't offer it on an asset's workflow, and publishing refuses it there.

**The three plugins that use these contracts are rebuilt with a version bump:** `Toda.Ai` (`AiStep`), `Toda.Email`
(`ConversationStep`) and `Toda.Sample.TestManagement` (`SignOffStep`). `[TypeForwardedTo]` doesn't help here: it keeps a
type's full name, and the namespace must change to `Toda.Workflows` before the package is public. The plugin check
(ADR 0078) already reads `Toda.*` assemblies, so an old build is refused with what it lacks instead of failing at
run time. No plugin outside this repository uses them yet.

### The runner

`RecordService.UpdateAsync` keeps its order and calls the runner at its four points. A transition is a run with
phases, and the host's own edits stay where they are between them:

| Phase | What | Today |
|---|---|---|
| `BeginAsync` | resolve the transition, check the right to change the status, start the trace, check conditions | lines 521–559 |
| (host) | the host applies the transition's fields and its own edits | lines 561–727 |
| `CompleteAsync` | checks against the values now, then the status through the store | lines 729–739 |
| (host) | done-only work: unfinished items, resolution | lines 741–763 |
| `FinishAsync` | actions, then entering the target (sync types leave at once, async ones plan an activity) | lines 765–773 |

The run is a `WorkflowRun` object the host holds between phases. `TransitionsAsync` (the open transitions),
`AdvanceAsync` (a waiting step's outcome) and `DecideAsync` (approvals) call the runner the same way. A host with no
edits of its own (assets, the marketplace) calls `TakeAsync`, which runs all three phases.

### Tables, schema and configuration

- **Schema `workflows`:** the six tables move with `SET SCHEMA`, not a copy: `workflows`, `workflow_versions`,
  `workflow_activities`, `approval_decisions`, `workflow_runs` and `workflow_run_steps`.
  - `record_id` becomes `item_id`, with a new `item_kind` (`record` for every row that's there).
  - `project_id` becomes `scope_id`, with `scope_kind`.
  - The migration ships in the package. Toda's runs in its order under the `startup-migrate` lock, after Projects.
- **The backup and the data map follow.** The backup's sources (ADR 0068) and the personal-data map (ADR 0082) name
  the new schema. Approval comments are personal data, as they are now.
- **The binding stays with the item's module**, as its own column or table: `record_types.workflow_id` for records,
  a schema's `workflow_id` for assets, the marketplace's own. The store answers it. An item with no workflow keeps
  today's behaviour: records get one made from their status list, assets set statuses freely.
- **The stored definition gets a format number:** `"format": 1`. A missing number reads as 1. A reader refuses a
  higher one with a message naming the version it needs. Once packaged, the JSON is a public contract. The designer's
  `diagram` stays its own and opaque.
- **Configuration: `WorkflowOptions`, from the "Workflows" section:** the hop limit (20), the sweep interval, retry
  backoff and the tracer's retention. Defaults are today's values.

### Assets: workflows of their own

The open question from the study was whether assets need workflows, or a record that moves an asset's status is
enough. **Assets get workflows of their own, opt-in per schema.** Retiring a laptop with an approval shows why:

- The approval belongs on the asset. Approvers open the asset, its history says who approved, and undo
  (`AssetUndo`) puts it back.
- A record for each retirement means a project, a type and a workflow made only to drive another item's status.
  That is the "engine that only knows one entity" this record avoids.
- A schema with no workflow keeps setting statuses freely, as now. Asset statuses' categories (active, pending,
  inactive) map to the workflow's (to do, in progress, done) in the asset store, one way and fixed.

The user chose this on 29 Oct, and asked for it next, straight after the seam.

### The marketplace

**The marketplace waits for the package** (the user, 29 Oct: building it right is the goal, not speed). There is no
stop-gap state machine. Its item kinds will be `listing`, `listing-version` and `publisher`. Its store has no
change sets, and its actors come from Guard. The AI review is a status type it registers over Toda's AI package,
once that exists (handover, item 6).

### Steps

Each step lands on main with `scripts/ci.sh` green, run from a clean `git archive` copy. Every step must pass these
gates:

- `e2e_workflows`, `e2e_ai_step`, `e2e_undo`, `e2e_automation`, `e2e_scheduling` and `e2e_delegation`;
- the OpenAPI drift check: the mobile app's `/api/records/{key}/transitions` and `/approvals` stay byte-identical;
- the "change a status" p95 under 200 ms on a 50,000-record project (`docs/load.md`), against the S1 baseline.

Code moves verbatim first. Behaviour changes come only in separate commits.

| Step | What | Size |
|---|---|---|
| S0 | this record | |
| S1 | Behaviour pinned: characterization tests for transitions (conditions, fields, checks, actions, self loops, any-to-any), entering each status type, the hop guard, approvals with delegation and admins, held submissions released into a workflow, the scheduler's moves in a transition's change set, undo moving a record back; the p95 measured and written down | M |
| S2 | `Toda.Workflows.Contracts`: the item contract, item-shaped contexts, `Feature<T>()`, `AppliesTo`, the format number; an architecture test (no ABP, EF Core, Toda or Casl); the three plugins rebuilt with a version bump | M |
| S3 | The runner lifted in slices, each its own commit with CI green: (a) open transitions and conditions, (b) checks and the status, (c) actions, entering and leaving, (d) advancing and approvals, (e) the worker and signals | L, the riskiest |
| S4 | The seams and Toda's adapters in `Toda.Workflows.Integration`; `Toda.Workflows` references no Toda assembly | M |
| S5 | The tables to schema `workflows`, `item_id`/`item_kind`, `scope_id`/`scope_kind`; backup sources and the data map | M |
| S6 | Stage 2, assets: a schema's workflow, the asset store, the designer in the schema's settings, `e2e_asset_workflows` retiring a laptop with an approval | L |
| S7 | A sample host with its own item (a document in its own table) and Postgres integration tests, using only the defaults | M |
| S8 | The designer as `@todahq/workflow-designer`: next-intl, `useApi` and the pickers injected | M |
| S9 | A short freeze on `src/Toda.Workflows*`; the history copied to `todahq/toda-workflow`, audited, published as 0.1.0-preview.1 on nuget.org with Trusted Publishing, as toda-guard is | |
| S10 | Toda switches to the packages and deletes `src/Toda.Workflows*`; the marketplace adopts them; the freeze lifts | |

The order is S1 to S6 first, so assets run workflows inside Toda. S7 to S10 follow with no date: nothing waits on
them.

S3 avoids a long freeze on Projects: each slice is small and lands while the agents and undo work go on. Whoever
touches `RecordService.UpdateAsync` in that window says so first.

### Not now

- **Status mappings when a published version drops a status in use.** Publishing refuses that today. Later it can be
  a job in the package that moves items through each store when a workflow changes.
- **In-process expression conditions.** `field-value` and scripts cover it. A cheap expression condition can come as
  an extension later, without touching the contract.
- **The field engine as a package.** Field access is its own seam, so this can be decided later.

### Amendment, 30 Oct 2026: the seams as built (S4)

Built from the engine's side, the seams came out a little differently from the table above:

- **Who may act is the item session's.** `WorkflowItemSession` answers the right to change the status, the actor's
  groups, whether they manage the scope, delegation of assignments and service accounts; for approvals the host passes
  an `ApprovalDecider` it has checked. There's no separate `IWorkflowActors`: the rights are the item's host's.
- **No locks seam.** The worker claims a waiting visit with a conditional update, and a signal locks its row with
  `FOR UPDATE`: both are plain Postgres, as every host has. `IWorkflowLocks` isn't needed.
- **Scopes stay in the workflow service** for now (a project's workflows and the site's); `IWorkflowScopes` waits for a
  host that needs other scopes.
- **`IWorkflowSiteContexts`** gives the checks' `IWorkflowSiteContext` for a scope. The default checks nothing (every
  account and group exists, every field is text); Toda's reads Guard and the field catalog.
- **`IWorkflowExtensionSource`** leases extensions and status types, with a generation for caches. The default has the
  host's own from DI; Toda's leases its plugins'.
- **`IWorkflowEvents`** says a transition was taken, an item advanced and an approval decided. Toda keeps the default,
  which tells no one: its records and assets say what happened in their own events.
- **`IWorkflowItemStore`** also counts the items sitting in statuses (a published version can't drop one items sit in)
  and is told of a publish (record types' status lists and asset schemas' status rows follow).
- **The engine's own error codes** (`WorkflowErrorCodes`), with Toda's strings until its rename.
- **Toda's side is a folder for now:** `src/Toda.Projects/Workflows/Integration` holds records as items, the record-only
  built-ins, scripts, customer replies, record-type binding, config as code and Toda's adapters. The engine can't leave
  `Toda.Projects` while its tables are in the projects context; with S5b it moves to `Toda.Workflows`, and the folder
  becomes `Toda.Workflows.Integration`. Until then an architecture test holds the engine's files to the seams.

### Amendment, 30 Oct 2026: the engine's tables in the host's context (S5b)

The engine doesn't get a `WorkflowsDbContext` of its own. A host's queries read its tables beside the host's own: RQL's
`approver = currentUser()` is one SQL query over records, waiting approvals and decisions, which two contexts can't
join. So the engine ships as ABP's modules do:

- **`IWorkflowsDbContext`**, an `IEfCoreDbContext` with the six tables, and **`ConfigureWorkflows(builder)`**, the
  mapping a host applies in its own `OnModelCreating`.
- **The host's context replaces it** (`[ReplaceDbContext]`, `ReplaceDbContext<IWorkflowsDbContext>()`): the engine's
  provider hands out the host's context, the same instance in the same change. The engine's `WorkflowsDb` runs its own
  work (the worker, signals, the tracer) in a unit of work, as the hosts' module helpers do.
- **The host's migrations make the tables**, in schema `workflows`; the engine keeps its stored JSON's format its own.
  Toda's live in the projects migrations. A host that wants them apart can still map them into a context of its own:
  it then gives up joining them with its items.

### Amendment, 30 Oct 2026: Toda's side stays in Toda.Projects (S5c)

The engine is `src/Toda.Workflows`, a module of its own (`TodaWorkflowsModule`) with nothing of Toda's in it. Toda's
side doesn't become `Toda.Workflows.Integration`: records are Toda's items, and `RecordService` and
`ProjectsDbContext` call the record store, session and type sync directly, so a separate project would need
`Toda.Projects` while `Toda.Projects` needs it. Those files stay in `src/Toda.Projects/Workflows`, beside the records
they adapt, as assets' adapters stay in `Toda.Assets`. A host's side of the engine lives with the host's items.

### Amendment, 31 Oct 2026: a host's own items through the item-kind contract (S7)

ADR 0090's `IWorkflowItemKind` is the simplest way for any host to move its own items, not only for plugins. A kind a
host registers in its services is named by its key alone ("document"); a plugin's is "{plugin}:{key}". The engine's
`WorkflowItems` does the rest for either: start, transitions, approvals, runs and bindings, in the engine's change.
A host still writes an `IWorkflowItemStore` and a `WorkflowItemSession` of its own when its items need more than the
contract gives (records' children and links, assets' history), as Toda's records and assets do. A host's own status
types are named `builtin:{key}`, as its own extensions are.

### Amendment, 31 Oct 2026: conditions, checks and actions

Before the first release, a transition's hooks are conditions, checks and actions. What were validators are checks; what
were post-functions are actions, the same word an automated status uses for what it runs. The kinds are `condition`,
`check` and `action`; a transition's lists are `checks` and `actions`; a run's phases are `check` and `action` (a
transition's action has no status key, an automated status's has its own). The engine still reads the earlier names
("validators", "postFunctions", and the kinds "validator" and "postFunction" from extensions built before), so exports,
backups and plugins made earlier keep working; Toda's migration `WorkflowHookNames` renames what it stores, and its
script check is `builtin:script-check`.

## Consequences

- **ADR 0001 is amended for workflows,** as ADR 0087 amended it for Guard: they leave this repository before the SDK
  freeze.
- **Three plugins are rebuilt.** A plugin built before S2 against `Toda.Projects.Workflows` is refused at install,
  saying what it lacks. The version installed before keeps running until then. Plugins built from S2 on bind to
  `Toda.Workflows.Contracts`.
- **The engine's tables become a contract for every host.** Released migrations are never edited, and the stored
  definition carries its format.
- **`RecordService` gets smaller but not simple.** Its own edits stay between the runner's phases. The runner takes
  the transition's half of `UpdateCoreAsync`, nothing more.
- **A change to how items move becomes a release** once S10 lands. While working, `TODA_WORKFLOW_CHECKOUT` swaps the
  package references for project references, as `TODA_GUARD_CHECKOUT` does.
- **To watch:**
  - the p95 after each S3 slice, since the runner adds a call boundary around a hot path;
  - transactions shared across two DbContexts under ABP's unit of work, especially `requiresNew`;
  - the plugin-facing names, which can't change again after 0.1.0 without another rebuild.
