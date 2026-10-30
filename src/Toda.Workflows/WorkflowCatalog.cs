using System.Text.Json;
using Microsoft.Extensions.Logging;
using Volo.Abp.DependencyInjection;

namespace Toda.Workflows;

/// <summary>
/// A status type (ADR 0019, 0022): manual, synchronous or asynchronous; the outcomes it must leave by (each mapped to a
/// status when the workflow is published) and the ones it may leave by. A plugin's type also has its plugin, its
/// settings schema (the designer's form), optionally its own edit component, and the settings property that names more
/// exits (its routes), which a status must map too.
/// </summary>
public sealed record StatusTypeInfo(string Key, string Mode, IReadOnlyList<string> Exits, string Title, string Description, IReadOnlyList<string>? OptionalExits = null,
    string? PluginKey = null, JsonElement? SettingsSchema = null, string? EditComponent = null, string? RoutesSetting = null)
{
    public const string ManualMode = "manual";
    public const string SyncMode = "sync";
    public const string AsyncMode = "async";
}

/// <summary>
/// The status types this site has (ADR 0022): the five built in, and those of enabled plugins
/// ("workflow.statusType@1"), keyed "{plugin}:{key}", read again whenever plugins change. The built-in types run in
/// the engine (they use its transitions and approvals); a plugin's run through <see cref="IWorkflowStatusType"/>.
/// </summary>
public class WorkflowCatalog(IWorkflowExtensionSource source, IServiceProvider services, ILogger<WorkflowCatalog> logger) : ISingletonDependency
{
    public const string Automated = "automated";
    public const string Decision = "decision";
    public const string Wait = "wait";
    public const string Approval = "approval";

    private (long Generation, IReadOnlyDictionary<string, StatusTypeInfo> Types) _snapshot = (-1, BuiltIn);

    public IReadOnlyDictionary<string, StatusTypeInfo> StatusTypes
    {
        get
        {
            (long generation, IReadOnlyDictionary<string, StatusTypeInfo> types) = _snapshot;
            if (generation == source.Generation)
            {
                return types;
            }

            Dictionary<string, StatusTypeInfo> built = new(BuiltIn, StringComparer.Ordinal);
            IWorkflowLease<IWorkflowStatusType> lease = source.Lease<IWorkflowStatusType>(services);
            try
            {
                foreach ((IWorkflowStatusType type, string? plugin) in lease.Items.Select(i => (i.Instance, i.PluginKey)))
                {
                    if (plugin is null || type.Mode is not (StatusTypeModes.Sync or StatusTypeModes.Async))
                    {
                        logger.LogWarning("A status type {Type} from {Plugin} is skipped: it needs a plugin and a mode of sync or async.", type.Key, plugin);
                        continue;
                    }

                    try
                    {
                        string key = $"{plugin}:{type.Key}";
                        built[key] = new StatusTypeInfo(key, type.Mode, [.. type.Exits], type.Title, type.Description, [.. type.OptionalExits], plugin,
                            JsonDocument.Parse(type.SettingsSchema).RootElement.Clone(), type.EditComponent, type.RoutesSetting);
                    }
                    catch (JsonException ex)
                    {
                        logger.LogWarning("A status type {Type} from {Plugin} is skipped: its settings schema isn't JSON ({Error}).", type.Key, plugin, ex.Message);
                    }
                }
            }
            finally
            {
                lease.DisposeAsync().AsTask().GetAwaiter().GetResult(); // a property: ends the plugins' scopes (ASP.NET Core has no sync context to block)
            }

            _snapshot = (source.Generation, built);
            return built;
        }
    }

    /// <summary>Whether a type is a plugin's ("{plugin}:{key}").</summary>
    public static bool IsPluginType(string type) => type.Contains(':', StringComparison.Ordinal);

    private static readonly Dictionary<string, StatusTypeInfo> BuiltIn = new(StringComparer.Ordinal)
    {
        [WorkflowStatus.Manual] = new(WorkflowStatus.Manual, StatusTypeInfo.ManualMode, [], "Manual", "Waits for a person to take a transition."),
        [Automated] = new(Automated, StatusTypeInfo.SyncMode, ["done"], "Automated",
            "Runs its actions as the record enters, then leaves at once."),
        [Decision] = new(Decision, StatusTypeInfo.SyncMode, ["otherwise"], "Decision",
            "Checks its routes' conditions in order as the record enters and leaves by the first that holds, otherwise by 'otherwise'."),
        [Wait] = new(Wait, StatusTypeInfo.AsyncMode, ["done"], "Wait", "Waits a number of minutes, or until a date field's day, then leaves."),
        [Approval] = new(Approval, StatusTypeInfo.AsyncMode, ["approved", "rejected"], "Approval",
            "Waits for approvers (people, groups read live, or a people field): any, all, or a number of them.", ["timedOut"]),
    };

    /// <summary>The outcomes a status must map: its type's, for a decision one per route, and the routes a plugin's type names in its settings.</summary>
    public IEnumerable<string> RequiredExits(WorkflowStatus status) =>
        StatusTypes.TryGetValue(status.Type, out StatusTypeInfo? type)
            ? type.Exits.Concat(status.Type == Decision ? Routes(status.Settings).Select(r => r.Exit) : SettingsRoutes(status.Settings, type.RoutesSetting)).Distinct()
            : [];

    /// <summary>The exits a plugin's type names in its settings (<see cref="IWorkflowStatusType.RoutesSetting"/>): names, or objects with an exit.</summary>
    public static IReadOnlyList<string> SettingsRoutes(JsonElement? settings, string? property)
    {
        if (property is null || settings is not { ValueKind: JsonValueKind.Object } s || !s.TryGetProperty(property, out JsonElement list) || list.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return [.. list.EnumerateArray()
            .Select(r => r.ValueKind == JsonValueKind.String ? r.GetString()
                : r.ValueKind == JsonValueKind.Object && r.TryGetProperty("exit", out JsonElement e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null)
            .OfType<string>().Where(e => e.Length > 0).Distinct()];
    }

    public bool IsAsync(string type) => StatusTypes.TryGetValue(type, out StatusTypeInfo? info) && info.Mode == StatusTypeInfo.AsyncMode;

    /// <summary>A decision's routes: an exit name and the conditions that send the record that way.</summary>
    public static List<(string Exit, ConditionGroup? When)> Routes(JsonElement? settings)
    {
        List<(string, ConditionGroup?)> routes = [];
        if (settings is { ValueKind: JsonValueKind.Object } s && s.TryGetProperty("routes", out JsonElement list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement route in list.EnumerateArray())
            {
                string? exit = route.TryGetProperty("exit", out JsonElement e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;
                ConditionGroup? when = route.TryGetProperty("when", out JsonElement w) && w.ValueKind == JsonValueKind.Object
                    ? w.Deserialize<ConditionGroup>(JsonSettings)
                    : null;
                if (exit is not null)
                {
                    routes.Add((exit, when));
                }
            }
        }

        return routes;
    }

    /// <summary>An automated status's actions: uses of action extensions.</summary>
    public static List<ExtensionUse> Actions(JsonElement? settings) =>
        settings is { ValueKind: JsonValueKind.Object } s && s.TryGetProperty("actions", out JsonElement list) && list.ValueKind == JsonValueKind.Array
            ? list.Deserialize<List<ExtensionUse>>(JsonSettings) ?? []
            : [];

    internal static readonly JsonSerializerOptions JsonSettings = JsonSerializerOptions.Web;
}
