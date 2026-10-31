# Getting started

This guide builds a host that moves its own documents through a review workflow. It is the sample host
(`samples/Toda.Workflows.SampleHost`), explained in order. Run the sample beside it to follow along.

You need an ABP application on .NET 10 with EF Core and PostgreSQL.

## 1. Add the package and the module

```bash
dotnet add package Toda.Workflows --prerelease
```

Depend on `TodaWorkflowsModule`. It brings ABP's EF Core module for PostgreSQL and registers the engine's services, its
defaults for every seam, the background worker and the trace retention.

```csharp
[DependsOn(typeof(AbpAutofacModule), typeof(AbpAspNetCoreMvcModule), typeof(TodaWorkflowsModule))]
public class SampleHostModule : AbpModule
{
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        context.Services.AddAbpDbContext<SampleDbContext>(options => options.ReplaceDbContext<IWorkflowsDbContext>());
        Configure<AbpDbContextOptions>(options => options.UseNpgsql());
        Configure<AbpExceptionHttpStatusCodeOptions>(options =>
        {
            options.Map(WorkflowErrorCodes.Conflict, HttpStatusCode.Conflict);
            options.Map(WorkflowErrorCodes.Forbidden, HttpStatusCode.Forbidden);
        });
    }
}
```

## 2. Map the engine's tables in your context

The engine doesn't have a database context of its own. It works on yours, so the item you change and the engine's
rows (approvals, runs, waiting steps) are saved in one transaction. Your context implements `IWorkflowsDbContext`,
says it replaces it, and applies the engine's mapping:

```csharp
[ConnectionStringName("Default")]
[ReplaceDbContext(typeof(IWorkflowsDbContext))]
public class SampleDbContext(DbContextOptions<SampleDbContext> options) : AbpDbContext<SampleDbContext>(options), IWorkflowsDbContext
{
    public DbSet<Document> Documents => Set<Document>();

    public DbSet<Workflow> Workflows => Set<Workflow>();
    public DbSet<WorkflowVersion> WorkflowVersions => Set<WorkflowVersion>();
    public DbSet<WorkflowActivity> WorkflowActivities => Set<WorkflowActivity>();
    public DbSet<ApprovalDecision> ApprovalDecisions => Set<ApprovalDecision>();
    public DbSet<WorkflowRun> WorkflowRuns => Set<WorkflowRun>();
    public DbSet<WorkflowRunStep> WorkflowRunSteps => Set<WorkflowRunStep>();
    public DbSet<WorkflowBinding> WorkflowBindings => Set<WorkflowBinding>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ConfigureWorkflows();   // schema "workflows"
        modelBuilder.Entity<Document>(b => b.ToTable("documents"));
    }
}
```

Then add a migration as you do for your own tables (`dotnet ef migrations add Workflows`). The sample calls
`EnsureCreatedAsync` instead, to stay short.

## 3. Keep a status and a version on your item

The engine needs three things from your entity: a status (a key of the workflow's statuses), a version that changes on
every write (so a stale write is refused), and a type (what binds it to a workflow). The sample's `Document` has
`Status`, `Version` and `Type` columns, plus its own fields (`Title`, `Body`, `Amount`, `OwnerId`, `ReviewerId`).

## 4. Describe your items to the engine

Write one class that implements `IWorkflowItemKind` and register it under that interface. The engine calls it, always
inside the change that moves the item, to:

- read an item (`OpenAsync`);
- ask what someone may do (`RightsAsync`);
- check a field a workflow sets (`CheckFieldAsync`);
- save what changed (`SaveAsync`);
- count items per status (`InStatusesAsync`), and move them when a type is rebound (`MoveStatusesAsync`).

```csharp
[ExposeServices(typeof(IWorkflowItemKind), typeof(DocumentKind))]
public class DocumentKind(IDbContextProvider<SampleDbContext> context) : IWorkflowItemKind, ITransientDependency
{
    public string Key => "document";
    public string Noun => "document";
    public string Title => "Documents";
    public string Scope => WorkflowItemScopes.Site;
    public IReadOnlyList<WorkflowItemType> Types { get; } = [new("memo", "Memo"), new("contract", "Contract")];

    public async Task<WorkflowItemSnapshot?> OpenAsync(Guid id, CancellationToken ct)
    {
        SampleDbContext db = await context.GetDbContextAsync();   // the caller's change, joined
        Document? d = await db.Documents.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        return d is null ? null : new WorkflowItemSnapshot(
            new WorkflowItemRef("document", d.Id, d.Key, d.Type, null, null), d.Status, d.Version,
            new Dictionary<string, JsonElement?> { ["title"] = JsonSerializer.SerializeToElement(d.Title), /* ... */ });
    }

    // RightsAsync, CheckFieldAsync, SaveAsync, InStatusesAsync, MoveStatusesAsync: see the sample's DocumentKind.cs
}
```

[Items](items.md) explains each member and the rules for values.

## 5. Make a workflow

A workflow is a definition: statuses, transitions and what they check and do. Make one from code with
`WorkflowService.CreateAsync`, which checks it as publishing does and saves it as version 1:

```csharp
await workflowsDb.RunAsync(async db =>
{
    await workflows.CreateAsync(db, scopeId: null, "memo-review", definition, actorId);
    await db.SaveChangesAsync();
}, requiresNew: true, isTransactional: true);
```

The sample takes the definition as JSON on `POST /api/workflows`. Here is the one its tests use:

- **draft**, a manual status.
- **review**, an approval by a group; approved goes on to *countersign*, rejected goes back to *draft*.
- **countersign** waits for an outside signal.
- **published** sends a notice and moves on to **archived**.
- The **submit** transition asks for the body, checks it, and names the reviewer.

```json
{
  "key": "memo-review",
  "definition": {
    "statuses": [
      { "key": "draft", "names": { "en": "Draft" }, "category": "todo" },
      { "key": "review", "names": { "en": "In review" }, "category": "inProgress", "type": "approval",
        "settings": { "groups": ["00000000-0000-0000-0000-0000000000f1"], "rule": "any" },
        "exits": { "approved": "countersign", "rejected": "draft" } },
      { "key": "countersign", "names": { "en": "Countersign" }, "category": "inProgress", "type": "builtin:countersign",
        "exits": { "signed": "published" } },
      { "key": "published", "names": { "en": "Published" }, "category": "done", "type": "builtin:notify",
        "settings": { "message": "Published" }, "exits": { "done": "archived" } },
      { "key": "archived", "names": { "en": "Archived" }, "category": "done" }
    ],
    "transitions": [
      { "key": "submit", "names": { "en": "Submit" }, "from": ["draft"], "to": "review", "fields": ["body"],
        "checks": [ { "type": "builtin:has-body", "settings": { "minLength": 10 } } ],
        "actions": [ { "type": "builtin:set-field", "settings": { "field": "reviewer", "value": "00000000-0000-0000-0000-0000000000b1" } } ] },
      { "key": "withdraw", "names": { "en": "Withdraw" }, "from": ["review"], "to": "draft",
        "conditions": { "match": "all", "conditions": [ { "type": "builtin:person", "settings": { "accounts": ["00000000-0000-0000-0000-0000000000c1"] } } ] } }
    ]
  }
}
```

[Workflow definitions](definitions.md) describes every field.

## 6. Bind a type of item to it

```csharp
await workflowItems.BindAsync(adminId, "document", "memo", scopeId: null, workflow: "memo-review");
```

Memos follow `memo-review` from now on. Binding a workflow that lacks a status some memos are in returns those counts
with nothing changed. Bind again with a map (`{ "draft": "open" }`) to move them.

## 7. Move items

Use the engine's `WorkflowItems` service, as yourself or as the person in the request:

```csharp
// A new item: save it in the workflow's first status, then let it enter (an approval asks, a wait starts).
BoundWorkflow bound = (await workflowItems.BoundAsync("document", "memo", null))!;
// ... save the document with Status = bound.Definition.InitialStatus.Key ...
await workflowItems.StartAsync(me, "document", id);

// What may I do now? Transitions whose conditions hold for me.
IReadOnlyList<WorkflowOpenTransition>? open = await workflowItems.TransitionsAsync(me, "document", id);

// Take one: conditions, the screen's fields, checks, the status, actions, the next status's entry.
WorkflowItemState state = await workflowItems.TakeAsync(me, "document", id, "submit", version: 1,
    fields: new Dictionary<string, JsonElement?> { ["body"] = JsonSerializer.SerializeToElement("Pizza on Friday") });

// Approvals: who may decide, and deciding.
IReadOnlyList<WorkflowItemApproval>? approvals = await workflowItems.ApprovalsAsync(me, "document", id);
await workflowItems.DecideAsync(reviewer, "document", id, approvals![0].Activity.Id, approve: true, comment: "Fine");

// History: what moved it, and with movedOnly false what was refused too.
List<WorkflowRun>? runs = await workflowItems.RunsAsync(me, "document", id, movedOnly: false);
```

A refusal is a `UserFriendlyException` that says why in words for the person. Its code is `WorkflowErrorCodes.Forbidden`
when a rule refused, or `WorkflowErrorCodes.Conflict` for a stale version or a decided approval. A check's refusal
has no code. Step 1 mapped the codes to 403 and 409.

## 8. Add your own extensions and steps

The sample adds three:

- a check, `builtin:has-body`;
- a step that notifies and moves on at once, `builtin:notify`;
- a step that waits for a signal, `builtin:countersign`, which the sample's `POST /api/documents/{id}/countersign`
  sends through `IWorkflowSignals`.

See [Extensions](extensions.md) and [Status types](status-types.md).

## 9. Tell the engine who's in which group

Approvals by group and the `person` condition ask `IWorkflowPeople` for an account's groups. The default knows nobody.
The sample reads groups from its settings; your host asks its directory. [Hosting](hosting.md) lists every seam.
