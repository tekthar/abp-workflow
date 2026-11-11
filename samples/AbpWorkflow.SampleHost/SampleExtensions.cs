using System.Collections.Concurrent;
using System.Text.Json;
using Volo.Abp.DependencyInjection;

namespace AbpWorkflow.SampleHost;

/// <summary>
/// A check of the host's own: a document needs a body before it's sent on. Registered in DI with no plugin, its
/// type in workflows is "builtin:has-body", beside the engine's built-ins. It works on documents only, so binding a
/// workflow that uses it to another kind is refused.
/// </summary>
[ExposeServices(typeof(IWorkflowExtension))]
public sealed class HasBodyCheck : IWorkflowExtension, ITransientDependency
{
    public string Key => "has-body";
    public string Kind => WorkflowExtensionKinds.Check;
    public IReadOnlyList<string> AppliesTo => [DocumentKind.Kind];
    public string Title => "Has a body";
    public string Description => "The document has some text in its body.";
    public string SettingsSchema => """{"type":"object","properties":{"minLength":{"type":"integer","title":"At least this many characters"}}}""";

    public string Summary(JsonElement? settings, string language) => "The body is filled in";

    public Task<string?> EvaluateAsync(WorkflowExtensionContext context, JsonElement? settings, CancellationToken cancellationToken)
    {
        int min = settings is { ValueKind: JsonValueKind.Object } s && s.TryGetProperty("minLength", out JsonElement m) && m.TryGetInt32(out int n) ? n : 1;
        string body = context.Values.GetValueOrDefault("body") is { ValueKind: JsonValueKind.String } b ? b.GetString()!.Trim() : "";
        return Task.FromResult(body.Length >= min ? null : $"{context.Item.Key} needs a body of at least {min} characters first.");
    }
}

/// <summary>What the sample's steps sent: a real host would mail or call out.</summary>
public sealed class SampleOutbox : ISingletonDependency
{
    public ConcurrentQueue<string> Sent { get; } = new();
}

/// <summary>
/// A status type of the host's own that leaves at once ("builtin:notify"): it sends a line to the outbox as the item
/// comes in, then leaves by "done". A sync type does its work inside the change that moved the item.
/// </summary>
[ExposeServices(typeof(IWorkflowStatusType))]
public sealed class NotifyStatusType(SampleOutbox outbox) : IWorkflowStatusType, ITransientDependency
{
    public string Key => "notify";
    public string Mode => StatusTypeModes.Sync;
    public IReadOnlyList<string> Exits => ["done"];
    public string Title => "Notify";
    public string Description => "Sends a message about the item, then moves on.";
    public string SettingsSchema => """{"type":"object","required":["message"],"properties":{"message":{"type":"string","title":"Message"}}}""";

    public Task<StatusEntryResult> EnterAsync(WorkflowStatusContext context, JsonElement? settings, CancellationToken cancellationToken)
    {
        string message = settings is { ValueKind: JsonValueKind.Object } s && s.TryGetProperty("message", out JsonElement m) ? m.GetString() ?? "" : "";
        outbox.Sent.Enqueue($"{context.Item.Key}: {message}");
        context.Log($"Sent \"{message}\"");
        return Task.FromResult(StatusEntryResult.Leave("done"));
    }
}

/// <summary>
/// A status type of the host's own that waits ("builtin:countersign"): the item stays until a signal says who signed
/// (<c>{ "by": accountId }</c>), sent through <see cref="IWorkflowSignals"/>, then leaves by "signed". A step that
/// waits for an outside system, a reply or a person elsewhere is built like this.
/// </summary>
[ExposeServices(typeof(IWorkflowStatusType))]
public sealed class CountersignStatusType : IWorkflowStatusType, ITransientDependency
{
    public string Key => "countersign";
    public string Mode => StatusTypeModes.Async;
    public IReadOnlyList<string> Exits => ["signed"];
    public string Title => "Countersign";
    public string Description => "Waits until someone countersigns the item elsewhere.";
    public string SettingsSchema => """{"type":"object","properties":{}}""";

    public Task<StatusEntryResult> EnterAsync(WorkflowStatusContext context, JsonElement? settings, CancellationToken cancellationToken) =>
        Task.FromResult(StatusEntryResult.Wait(null)); // only a signal ends it

    public Task<StatusResumeResult> ResumeAsync(WorkflowStatusContext context, JsonElement? settings, JsonElement? signal, CancellationToken cancellationToken) =>
        Task.FromResult(signal is { ValueKind: JsonValueKind.Object } s && s.TryGetProperty("by", out JsonElement by) && by.ValueKind == JsonValueKind.String
            ? StatusResumeResult.Leave("signed")
            : StatusResumeResult.KeepWaiting(null));
}

/// <summary>
/// Who people are, for approvals by group and the person condition: the sample's groups are in its settings
/// (Sample:Groups:{groupId} lists account ids). A real host asks its directory.
/// </summary>
[Dependency(ReplaceServices = true)]
[ExposeServices(typeof(IWorkflowPeople))]
public sealed class SamplePeople(IConfiguration configuration) : IWorkflowPeople, ITransientDependency
{
    public Task<IReadOnlyList<Guid>> GroupIdsAsync(Guid accountId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<Guid>>([.. configuration.GetSection("Sample:Groups").GetChildren()
            .Where(g => g.Get<Guid[]>()?.Contains(accountId) == true)
            .Select(g => Guid.Parse(g.Key))]);

    public Task<bool> IsServiceAccountAsync(Guid accountId, CancellationToken cancellationToken) => Task.FromResult(false);
}
