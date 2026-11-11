using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Volo.Abp;
using Volo.Abp.AspNetCore.Mvc;
using Volo.Abp.Domain.Entities;

namespace AbpWorkflow.SampleHost;

public sealed record CreateWorkflowInput(string Key, WorkflowDefinition Definition, Dictionary<string, string>? Names = null);

public sealed record BindInput(string? Workflow, Dictionary<string, string>? Map = null, bool Apply = true);

public sealed record CreateDocumentInput(string Type, string Title, string? Body = null, decimal? Amount = null, Guid? Reviewer = null);

public sealed record TakeInput(int? Version = null, Dictionary<string, JsonElement?>? Fields = null);

public sealed record DecideInput(bool Approve, string? Comment = null);

public sealed record SignInput(Guid By);

/// <summary>
/// The sample's API: workflows made from JSON, a document type bound to one, and documents moving through it. Who's
/// acting is the X-Account header (an account id): the sample has no sign-in, a real host has its own. Every call that
/// moves a document goes through the engine's <see cref="WorkflowItems"/>.
/// </summary>
[Route("api")]
public class DocumentsController(WorkflowItems items, WorkflowService workflows, WorkflowsDb workflowsDb, SampleDb sampleDb, IWorkflowSignals signals,
    SampleOutbox outbox) : AbpControllerBase
{
    private Guid Actor => Request.Headers.TryGetValue("X-Account", out var value) && Guid.TryParse(value, out Guid id)
        ? id
        : throw new UserFriendlyException("Say who you are in the X-Account header.", WorkflowErrorCodes.Forbidden);

    /// <summary>A site workflow from its definition, published as version 1.</summary>
    [HttpPost("workflows")]
    public Task<object> CreateWorkflow([FromBody] CreateWorkflowInput input, CancellationToken ct) =>
        workflowsDb.RunAsync<object>(async db =>
        {
            Workflow workflow = await workflows.CreateAsync(db, null, input.Key, input.Definition, Actor, input.Names, ct);
            await db.SaveChangesAsync(ct);
            return new { workflow.Id, workflow.Key, workflow.Version };
        }, requiresNew: true, ct, isTransactional: true);

    /// <summary>A document type follows a workflow from now on (or none); documents in statuses it lacks need a map.</summary>
    [HttpPut("document-types/{type}/workflow")]
    public Task<WorkflowBindResult> Bind(string type, [FromBody] BindInput input, CancellationToken ct) =>
        items.BindAsync(Actor, DocumentKind.Kind, type, null, input.Workflow, input.Map, input.Apply, cancellationToken: ct);

    /// <summary>A new document, in its workflow's first status, which it then enters (an approval asks, a step starts).</summary>
    [HttpPost("documents")]
    public async Task<WorkflowItemState> Create([FromBody] CreateDocumentInput input, CancellationToken ct)
    {
        Guid me = Actor;
        BoundWorkflow bound = await items.BoundAsync(DocumentKind.Kind, input.Type, null, ct)
            ?? throw new UserFriendlyException($"{input.Type} documents follow no workflow yet: bind one first.");
        Guid id = await sampleDb.RunAsync(async db =>
        {
            int number = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.CountAsync(db.Documents, ct) + 1;
            Document document = new(Guid.NewGuid(), $"DOC-{number}", input.Type, input.Title, me, bound.Definition.InitialStatus!.Key)
            {
                Body = input.Body,
                Amount = input.Amount,
                ReviewerId = input.Reviewer,
            };
            db.Documents.Add(document);
            await db.SaveChangesAsync(ct);
            return document.Id;
        }, ct);
        return await items.StartAsync(me, DocumentKind.Kind, id, ct);
    }

    [HttpGet("documents/{id:guid}")]
    public async Task<WorkflowItemState> Get(Guid id, CancellationToken ct) =>
        await items.GetAsync(Actor, DocumentKind.Kind, id, ct) ?? throw new EntityNotFoundException(typeof(Document), id);

    /// <summary>The transitions the person may take now: those whose conditions hold for them.</summary>
    [HttpGet("documents/{id:guid}/transitions")]
    public async Task<IEnumerable<object>> Transitions(Guid id, CancellationToken ct) =>
        (await items.TransitionsAsync(Actor, DocumentKind.Kind, id, ct) ?? throw new EntityNotFoundException(typeof(Document), id))
        .Select(t => new { t.Transition.Key, Name = t.Transition.Name(), To = t.To.Key, t.Transition.Fields });

    [HttpPost("documents/{id:guid}/transitions/{transition}")]
    public Task<WorkflowItemState> Take(Guid id, string transition, [FromBody] TakeInput? input, CancellationToken ct) =>
        items.TakeAsync(Actor, DocumentKind.Kind, id, transition, input?.Version, input?.Fields, ct);

    [HttpGet("documents/{id:guid}/approvals")]
    public async Task<IEnumerable<object>> Approvals(Guid id, CancellationToken ct) =>
        (await items.ApprovalsAsync(Actor, DocumentKind.Kind, id, ct) ?? throw new EntityNotFoundException(typeof(Document), id))
        .Select(a => new
        {
            a.Activity.Id, Status = a.Activity.StatusKey, a.Activity.State, a.Activity.Outcome, a.Activity.Required, a.MayDecide,
            Decisions = a.Decisions.Select(d => new { d.AccountId, d.Approved, d.Comment, d.AsAdmin }),
        });

    [HttpPost("documents/{id:guid}/approvals/{approvalId:guid}")]
    public Task<WorkflowItemState> Decide(Guid id, Guid approvalId, [FromBody] DecideInput input, CancellationToken ct) =>
        items.DecideAsync(Actor, DocumentKind.Kind, id, approvalId, input.Approve, input.Comment, ct);

    /// <summary>The document's runs, newest first: what moved it, and with <c>all</c> what was refused too.</summary>
    [HttpGet("documents/{id:guid}/runs")]
    public async Task<IEnumerable<object>> Runs(Guid id, [FromQuery] bool all, CancellationToken ct) =>
        (await items.RunsAsync(Actor, DocumentKind.Kind, id, movedOnly: !all, ct) ?? throw new EntityNotFoundException(typeof(Document), id))
        .Select(r => new { r.Kind, r.Transition, r.FromStatus, r.ToStatus, r.EndStatus, r.Outcome, r.Message });

    /// <summary>Someone countersigned elsewhere: the step waiting for it hears so, and the document moves on.</summary>
    [HttpPost("documents/{id:guid}/countersign")]
    public async Task<WorkflowItemState> Countersign(Guid id, [FromBody] SignInput input, CancellationToken ct)
    {
        PendingStep step = (await signals.PendingAsync(id, ct)).FirstOrDefault(s => s.StatusType == "builtin:countersign")
            ?? throw new UserFriendlyException("Nothing is waiting for a countersignature.", WorkflowErrorCodes.Conflict);
        await signals.SignalAsync(step.ActivityId, JsonSerializer.SerializeToElement(new { by = input.By.ToString() }), ct);
        return await Get(id, ct);
    }

    [HttpGet("outbox")]
    public IEnumerable<string> Outbox() => outbox.Sent;
}
