using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Volo.Abp.DependencyInjection;
using Volo.Abp.EntityFrameworkCore;

namespace AbpWorkflow.SampleHost;

/// <summary>
/// Documents as the engine sees them: the one class a host writes to move its own items by workflows. The engine keeps
/// the item open while it moves and calls back here, always in the change that moves it, to read it, ask who may do
/// what, check a field a workflow sets, and save what changed. Registered in DI (no plugin), its kind is "document".
/// </summary>
[ExposeServices(typeof(IWorkflowItemKind), typeof(DocumentKind))]
public class DocumentKind(IDbContextProvider<SampleDbContext> context, IConfiguration configuration) : IWorkflowItemKind, ITransientDependency
{
    public const string Kind = "document";

    public string Key => Kind;
    public string Noun => "document";
    public string Title => "Documents";

    /// <summary>The sample has no projects: documents are the site's.</summary>
    public string Scope => WorkflowItemScopes.Site;

    public IReadOnlyList<WorkflowItemType> Types { get; } = [new("memo", "Memo"), new("contract", "Contract")];

    /// <summary>Its values by name and shape, so a workflow that reads one (an approver field) is checked as it's saved.</summary>
    public IReadOnlyList<WorkflowItemField> Fields { get; } =
    [
        new("title", "text"), new("body", "text"), new("amount", "number"), new("owner", "person"), new("reviewer", "person"),
    ];

    public async Task<WorkflowItemSnapshot?> OpenAsync(Guid id, CancellationToken cancellationToken)
    {
        SampleDbContext db = await context.GetDbContextAsync(); // the caller's change, joined
        return await db.Documents.AsNoTracking().FirstOrDefaultAsync(d => d.Id == id, cancellationToken) is { } document ? Snapshot(document) : null;
    }

    /// <summary>
    /// Who may do what. Anyone known reads and moves documents; the site's admins (Sample:Admins) also decide approvals
    /// for the approvers. A real host asks its own permissions here.
    /// </summary>
    public Task<WorkflowItemRights> RightsAsync(WorkflowItemSnapshot item, Guid accountId, CancellationToken cancellationToken)
    {
        bool admin = configuration.GetSection("Sample:Admins").Get<Guid[]>()?.Contains(accountId) == true;
        return Task.FromResult(new WorkflowItemRights(Read: true, Move: true, ManagesScope: admin));
    }

    /// <summary>The fields a workflow may set (an action, a transition's screen), checked as any write is.</summary>
    public Task<JsonElement?> CheckFieldAsync(WorkflowItemSnapshot item, string field, JsonElement? value, CancellationToken cancellationToken) =>
        Task.FromResult(field switch
        {
            "body" or "title" when value is null or { ValueKind: JsonValueKind.String } => value,
            "amount" when value is null or { ValueKind: JsonValueKind.Number } => value,
            "reviewer" when value is null || (value is { ValueKind: JsonValueKind.String } v && Guid.TryParse(v.GetString(), out _)) => value,
            "body" or "title" or "amount" or "reviewer" => throw new WorkflowItemException($"That isn't a {field}."),
            _ => throw new WorkflowItemException($"A workflow can't set '{field}' on a document."),
        });

    /// <summary>Writes the status and fields, in the caller's change, refusing a stale version: it commits with the engine's own rows, or neither does.</summary>
    public async Task SaveAsync(WorkflowItemChange change, CancellationToken cancellationToken)
    {
        SampleDbContext db = await context.GetDbContextAsync();
        Document document = await db.Documents.FirstAsync(d => d.Id == change.Item.Id, cancellationToken);
        if (document.Version != change.Version)
        {
            throw new WorkflowItemException($"{document.Key} was changed by someone else first.", conflict: true);
        }

        document.Status = change.Status;
        foreach ((string field, JsonElement? value) in change.Fields)
        {
            switch (field)
            {
                case "title": document.Title = value?.GetString() ?? document.Title; break;
                case "body": document.Body = value?.GetString(); break;
                case "amount": document.Amount = value?.GetDecimal(); break;
                case "reviewer": document.ReviewerId = value?.GetString() is { } id ? Guid.Parse(id) : null; break;
            }
        }

        document.Version++;
    }

    public async Task<IReadOnlyDictionary<string, int>> InStatusesAsync(WorkflowItemSelection items, IReadOnlyCollection<string>? statuses,
        CancellationToken cancellationToken)
    {
        SampleDbContext db = await context.GetDbContextAsync();
        return await db.Documents.Where(d => d.Type == items.Type && (statuses == null || statuses.Contains(d.Status)))
            .GroupBy(d => d.Status).ToDictionaryAsync(g => g.Key, g => g.Count(), cancellationToken);
    }

    /// <summary>Binding a workflow that lacks some statuses: the documents in them move to the ones the admin chose.</summary>
    public async Task MoveStatusesAsync(WorkflowItemSelection items, IReadOnlyDictionary<string, string> map, Guid changeSetId, Guid actorId,
        CancellationToken cancellationToken)
    {
        SampleDbContext db = await context.GetDbContextAsync();
        List<string> from = [.. map.Keys];
        foreach (Document document in await db.Documents.Where(d => d.Type == items.Type && from.Contains(d.Status)).ToListAsync(cancellationToken))
        {
            document.Status = map[document.Status];
            document.Version++;
        }
    }

    private static WorkflowItemSnapshot Snapshot(Document d) => new(
        new WorkflowItemRef(Kind, d.Id, d.Key, d.Type, null, null), d.Status, d.Version,
        new Dictionary<string, JsonElement?>
        {
            ["title"] = JsonSerializer.SerializeToElement(d.Title),
            ["body"] = d.Body is null ? null : JsonSerializer.SerializeToElement(d.Body),
            ["amount"] = d.Amount is null ? null : JsonSerializer.SerializeToElement(d.Amount),
            ["owner"] = JsonSerializer.SerializeToElement(d.OwnerId.ToString()),
            ["reviewer"] = d.ReviewerId is null ? null : JsonSerializer.SerializeToElement(d.ReviewerId.ToString()),
        });
}
