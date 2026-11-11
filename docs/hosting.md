# Hosting

What a host gives the engine, and what to know to run it.

## Requirements

- .NET 10 and ABP 10.6 (`Volo.Abp.EntityFrameworkCore.PostgreSql`).
- PostgreSQL. The engine relies on it in two places:
  - signals lock their activity row with `FOR UPDATE`;
  - a binding's key is unique with nulls not distinct.
- Autofac or the default container, as ABP uses.

The engine has no controllers or pages: your host has its own API, sign-in and rights, and calls `WorkflowItems`,
`WorkflowService` and `IWorkflowSignals`.

## The seams

`AbpWorkflowModule` registers a default for each with `TryAdd`. Replace the ones you have your own of in your module
(`context.Services.Replace(ServiceDescriptor.Transient<IWorkflowPeople, MyPeople>())`), or with
`[Dependency(ReplaceServices = true)]`.

| Seam | Says | Default |
|---|---|---|
| `IWorkflowPeople` | An account's groups (for approvals by group and the person condition), and whether it's a service account. | Nobody is in any group. |
| `IWorkflowSiteContexts` | Whether the accounts, groups and fields a workflow's settings name exist in a scope, each field's shape, and the kind of item the workflow is for if you know it as it's saved. | Every account and group exists; a field has the shape an item kind declares, else text; the kind isn't known, so `AppliesTo` is checked when the workflow is bound. |
| `IWorkflowEvents` | A transition was taken, an item moved on from a step, an approval was decided: inside the change that did it. | Tells no one. |
| `IWorkflowExtensionSource` | Where extensions, status types and item kinds come from, leased per use, with a generation that changes when they do. | The host's own, from DI. |
| `IWorkflowJoinSwitch` | Whether the engine's own units of work join the caller's instead (when several changes must be one transaction). | Never. |
| `IWorkflowItemStore` | A store of your own for a kind (see [Items](items.md)). | Item kinds go through the engine's bridge. |

### Events

`IWorkflowEvents` is called inside the transaction. Use it to write an outbox row, a history line or a domain event
your host publishes after commit. Don't call out to other systems from it: if the change rolls back, the call can't be
undone.

### Plugins

A host with plugins implements `IWorkflowExtensionSource`. Its `Lease<T>(services)` returns the host's own items (no
plugin key), then each plugin's with its key, for one use. The engine disposes the lease right after, so a plugin can
be unloaded. Raise `Generation` when plugins change: the catalog of status types is cached by it, and the worker
retries what waited for a plugin that was off.

Plugins reference only `AbpWorkflow.Contracts`. Their types are named `{plugin}:{key}`.

## The database

- **Tables:**
  - `workflows.workflows` and `workflows.workflow_versions`;
  - `workflows.workflow_bindings`;
  - `workflows.workflow_activities` and `workflows.approval_decisions`;
  - `workflows.workflow_runs` and `workflows.workflow_run_steps`.
- **Your context maps them** with `modelBuilder.ConfigureWorkflows()`, implements `IWorkflowsDbContext`, and replaces
  it (`[ReplaceDbContext(typeof(IWorkflowsDbContext))]` and `options.ReplaceDbContext<IWorkflowsDbContext>()`). The
  engine's `WorkflowsDb` then works on your context, in your unit of work.
- **Your migrations make the tables.** Add one after adding `ConfigureWorkflows()`, and again after upgrading the
  package when its CHANGELOG says the model changed.
- **One context, joins included:** your queries can join the engine's tables, such as "items waiting for my approval".
  A host that wants them in a context of its own can map them there instead, and gives up those joins and the shared
  transaction.
- Definitions are stored as JSON with their format number.

## Units of work

- `WorkflowItems` methods that change something begin a new transactional unit of work (`requiresNew`). Call them
  outside your own change, or make the change through the kind's `SaveAsync`.
- The kind's callbacks run inside that unit of work: get your context from `IDbContextProvider<T>` and never commit.
- The worker and signals run their own units of work.
- A refused or failed transition's trace is written in a unit of work of its own (`WorkflowsDb.ApartAsync`), so it
  survives the rollback.

## Errors and HTTP

Refusals are ABP `UserFriendlyException`s with a message for the person:

| Code | When | Suggested status |
|---|---|---|
| `WorkflowErrorCodes.Forbidden` (`AbpWorkflow:Forbidden`) | A condition or rule refused; the actor may not change the status or decide. | 403 |
| `WorkflowErrorCodes.Conflict` (`AbpWorkflow:Conflict`) | A stale version; an approval already decided or over; a kind or plugin that's off. | 409 |
| none | A check's complaint, a bad request (no such transition, a field the screen doesn't ask for). | 400 (ABP answers 403 unless you map it) |

Map them with `AbpExceptionHttpStatusCodeOptions` or your own `IHttpExceptionStatusCodeFinder`. A missing item or
approval is ABP's `EntityNotFoundException` (404).

## Settings

| Setting | Default | Meaning |
|---|---|---|
| `Workflows:Trace:StepDays` | 30 | Days a run's steps are kept. |
| `Workflows:Trace:RunDays` | 365 | Days runs are kept. |

Bind another section to `WorkflowTraceOptions` after the engine's to keep names of your own.

## Background services

- **`WorkflowWorker`** sweeps every two seconds, in every server. Servers claim activities with a conditional update,
  so you can run any number of them.
- **`WorkflowTraceRetention`** deletes old traces every hour, in batches of 5,000.

Both run as ASP.NET Core hosted services. Their logs:

- "The workflow sweep failed; it runs again shortly."
- "Moving an item on from {Status} failed (attempt n): …"
- "Pruning workflow traces failed; it runs again in an hour."

## Personal data

The engine stores account ids:

- who acted, on runs and decisions;
- approvers, on activities;
- who published, on versions.

It also stores decisions' comments, and what extensions log in run steps. The log lines may repeat an item's values.
Runs and steps are deleted by retention; decisions and activities stay with their item. To erase a person, your host
deletes or rewrites those rows with its own.
