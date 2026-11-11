# 0001. Independent of any host

- **Status:** Accepted
- **Date:** 2026-11-11

## Context

AbpWorkflow began as the workflow engine of one product, and was made a package so other products could move their
own things through it: documents, orders, requests, listings. A host that serves its own customers can't take a
package that carries another product's name, or one that needs another product's platform to run.

## Decision

**The package knows nothing of its hosts.** Everything a host owns reaches the engine through a seam, each with a
default, so a host replaces only what it has:

| Seam | What it answers | Default |
|---|---|---|
| `IWorkflowItemKind` | the items of a kind: find, read, who may, save | none: the host or a plugin gives it |
| `IWorkflowItemStore` | a store of the host's own, for items with more to them than a status and values | the engine's bridge to item kinds |
| `IWorkflowPeople` | an account's groups, and whether it's a service account | nobody is in any group |
| `IWorkflowSiteContexts` | whether the people, groups and fields a workflow names exist in a scope | everything exists |
| `IWorkflowExtensionSource` | where extensions, status types and item kinds come from | the host's own, from DI |
| `IWorkflowJoinSwitch` | whether the engine's own units of work join the caller's | never |
| `IWorkflowEvents` | tell the host what happened, inside the change that did it | nothing |

- **`AbpWorkflow.Contracts` is plain .NET:** nothing of ABP, EF Core or any host. `AbpWorkflow` is an ABP module that
  needs only .NET, ABP, EF Core with Npgsql and its contracts. The architecture tests in `tests/AbpWorkflow.Tests`
  hold both.
- **The tables are the host's:** they live in the `workflows` schema, mapped in the host's own EF Core context
  (`WorkflowsModel.ConfigureWorkflows`, `IWorkflowsDbContext`), and the host owns their migrations.
- **Nothing of a host's name:** ids, namespaces, error codes (`AbpWorkflow:...`) and package metadata are neutral.
  `scripts/check-names.sh` fails CI if a former host's name comes back, in the sources and inside the packed
  packages; the CHANGELOG is the one place it may stand, to tell clients what was renamed.
- **Comments don't cite records the repository doesn't ship.** A design decision a reader needs is written here, in
  `docs/adr`, or in the guides.

## Consequences

- A host writes an adapter per seam it has, and the package never forks for one host.
- Renaming the package changed no table, column or stored value, so a host's migration for it is empty.
- Every new feature has two halves: the package's, behind a seam, and the host's adapter.
