# Toda Workflow

A workflow engine for ABP applications on .NET 10 and PostgreSQL. Your items move through statuses by transitions that
are offered when their conditions hold, let through when their checks pass, and followed by their actions. Statuses can
wait for approvals, timers or outside signals, and every run leaves a trace. Toda Workflow is the engine of Toda, an
open-source work management platform. It is packaged so any product can move its own things (documents, orders,
requests, listings) through the same engine.

> **Preview.** 0.x versions may change their API in any minor version (SemVer). Pin an exact version.

## What it does

- **Workflows as data:** statuses with a category (to do, in progress, done), transitions between them, drafts and
  published versions. The definition is JSON with a format number, checked when it's saved and when it's published.
- **Transitions that check and act:**
  - **conditions** decide who sees a transition;
  - **checks** look at the item as the person takes it, and can refuse;
  - **actions** change it afterwards.
  Each can be built in, the host's own, or a plugin's, through one contract.
- **Statuses that do something:**
  - **automated:** run actions and move on;
  - **decision:** routes by conditions;
  - **wait:** minutes, or until a date;
  - **approval:** people, groups or a people field; any, all or a number of them; with a timeout;
  - **your own step types:** ones that leave at once, or ones that wait for a time or an outside signal.
- **Your items, your tables:** the engine never touches your entity. It reads, checks and saves through one interface
  you write, in your transaction, so the item and the engine's rows commit together or not at all.
- **A background worker** ends waits, times out approvals, resumes steps and retries a failed move with back-off. Any
  number of servers can run it at once.
- **A tracer:** every run, refused ones too, with each condition, check, action and exit it went through, and
  lines the extensions log.
- **Bindings:** each type of item follows a workflow, at the site or per scope (a project, a department). Rebinding
  moves items out of statuses the new workflow lacks, by a map you give.

The engine keeps its seven tables in the `workflows` schema of your database, mapped in your own EF Core context.

## Packages

On [nuget.org](https://www.nuget.org/packages?q=Toda.Workflows):

| Package | What it holds |
|---|---|
| `Toda.Workflows.Contracts` | The public contracts: items (`IWorkflowItemKind`), extensions (`IWorkflowExtension`), status types (`IWorkflowStatusType`), signals and events. Nothing but .NET: plugins and extension libraries reference only this. |
| `Toda.Workflows` | The ABP module: definitions and publishing, the runner, built-in extensions and status types, approvals, the worker, the tracer, and the EF Core model you map. |

## Use it in a host

`samples/Toda.Workflows.SampleHost` is a complete host: documents in their own table, moving by workflows with the
engine's defaults. [Getting started](docs/getting-started.md) walks through it. In short:

```csharp
// 1. Depend on the module.
[DependsOn(typeof(TodaWorkflowsModule), typeof(AbpAspNetCoreMvcModule))]
public class MyModule : AbpModule
{
    public override void ConfigureServices(ServiceConfigurationContext context) =>
        // 2. Your context stands in for the engine's, so both write in one transaction.
        context.Services.AddAbpDbContext<MyDbContext>(o => o.ReplaceDbContext<IWorkflowsDbContext>());
}

// 3. Your context maps the engine's tables beside yours.
[ReplaceDbContext(typeof(IWorkflowsDbContext))]
public class MyDbContext(DbContextOptions<MyDbContext> options) : AbpDbContext<MyDbContext>(options), IWorkflowsDbContext
{
    public DbSet<Workflow> Workflows => Set<Workflow>();
    // ... the other six DbSets of IWorkflowsDbContext
    protected override void OnModelCreating(ModelBuilder b) { base.OnModelCreating(b); b.ConfigureWorkflows(); }
}

// 4. Tell the engine about your items: read, rights, check a field, save.
[ExposeServices(typeof(IWorkflowItemKind))]
public class OrderKind : IWorkflowItemKind, ITransientDependency { /* see docs/items.md */ }

// 5. Move them.
await workflowItems.BindAsync(admin, "order", "purchase", scopeId: null, workflow: "purchase-approval");
await workflowItems.TakeAsync(me, "order", orderId, "submit", version: order.Version);
```

## Guides

- [Getting started](docs/getting-started.md): a host from nothing to a document that's approved, step by step.
- [Concepts](docs/concepts.md): workflows, versions, statuses and categories, transitions, items, kinds and types,
  scopes, bindings, runs, the worker.
- [Workflow definitions](docs/definitions.md): the JSON, field by field, with the built-in status types' settings.
- [Items](docs/items.md): `IWorkflowItemKind`, the `WorkflowItems` service, and when to write a store of your own.
- [Extensions](docs/extensions.md): the built-in conditions, checks and actions, and writing your own.
- [Status types](docs/status-types.md): steps of your own that leave at once or wait for a time or a signal.
- [Hosting](docs/hosting.md): the seams (people, sites, events, plugins), the database and migrations, units of work,
  error codes and HTTP, settings, running several servers.
- [Troubleshooting](docs/troubleshooting.md): the messages you may meet, and what to do.

Comments in the code cite design records by number, such as "ADR 0089". These are Toda's architecture decision records.
`docs/adr/0089-workflows-as-a-package.md` is the one that made the engine a package.

## Build and test

You need the .NET 10 SDK. The host tests also need PostgreSQL: `localhost:5432` with user and password `postgres` (the
official image's defaults), or the server `TODA_WORKFLOW_TEST_POSTGRES` names.

```bash
docker run -d -p 5432:5432 -e POSTGRES_PASSWORD=postgres postgres:17
dotnet build Toda.Workflows.slnx -c Release
dotnet test Toda.Workflows.slnx -c Release
dotnet pack Toda.Workflows.slnx -c Release -o artifacts
```

To try the sample, run it and call its API with an `X-Account` header (the sample has no sign-in):

```bash
dotnet run --project samples/Toda.Workflows.SampleHost --urls http://localhost:5310
```

## Releasing

The version is `VersionPrefix` and `VersionSuffix` in `Directory.Build.props`, and `CHANGELOG.md` needs an entry for
it. Pushing the tag `v<version>`, such as `v0.1.0-preview.1`, runs the checks. GitHub Actions then pushes the packages
to nuget.org and makes a GitHub release.

Publishing uses nuget.org's Trusted Publishing, so no API key is stored anywhere: nuget.org gives the release job a key
that lasts an hour. It needs a Trusted Publishing policy on nuget.org for this repository, the workflow file `ci.yml`
and the environment `release`, and the repository variable `NUGET_USER` set to the nuget.org account that owns the
packages.

## Contributing

Issues and pull requests are welcome: see [CONTRIBUTING.md](CONTRIBUTING.md), and [SECURITY.md](SECURITY.md) for
vulnerabilities. CI runs on GitHub Actions for every pull request.

## License

Apache License 2.0: see [LICENSE](LICENSE) and [NOTICE](NOTICE).
