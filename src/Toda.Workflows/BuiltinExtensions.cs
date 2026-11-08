using System.Text.Json;
using System.Text.RegularExpressions;
using Volo.Abp.DependencyInjection;

namespace Toda.Workflows;

/// <summary>Helpers for extensions, the engine's and a host's: settings and stored values as plain strings.</summary>
public static class ExtensionHelpers
{
    public static string? Str(JsonElement? settings, string name) =>
        settings is { ValueKind: JsonValueKind.Object } s && s.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    public static List<string> Strings(JsonElement? settings, string name) =>
        settings is { ValueKind: JsonValueKind.Object } s && s.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Where(i => i.ValueKind == JsonValueKind.String).Select(i => i.GetString()!).ToList()
            : [];

    /// <summary>A stored value as the strings it holds: one for a scalar, each item of a list, none when empty.</summary>
    public static List<string> Texts(IReadOnlyDictionary<string, JsonElement?> values, string field) =>
        values.TryGetValue(field, out JsonElement? value) && value is { } v ? v.ValueKind switch
        {
            JsonValueKind.Null or JsonValueKind.Undefined => [],
            JsonValueKind.String => string.IsNullOrEmpty(v.GetString()) ? [] : [v.GetString()!],
            JsonValueKind.Array => v.EnumerateArray().Select(i => i.ValueKind == JsonValueKind.String ? i.GetString()! : i.GetRawText()).ToList(),
            _ => [v.GetRawText()],
        } : [];

    public static string List(IEnumerable<string> items) => string.Join(", ", items);

    public static Task<IReadOnlyList<string>> FieldsExistAsync(IWorkflowSiteContext site, IEnumerable<string> fields, CancellationToken ct) =>
        Problems(fields.Select(async f => await site.FieldKindAsync(f, ct) is null ? $"There's no field '{f}'." : null));

    /// <summary>
    /// The checks' problems, each check run after the one before: they share the request's database context, which takes
    /// one query at a time (two people and a group checked together used to fail with "a second operation was started").
    /// </summary>
    public static async Task<IReadOnlyList<string>> Problems(IEnumerable<Task<string?>> checks)
    {
        List<string> problems = [];
        foreach (Task<string?> check in checks)
        {
            if (await check is { } problem)
            {
                problems.Add(problem);
            }
        }

        return problems;
    }
}

/// <summary>Holds when a field has (or hasn't) a value, or one of some values.</summary>
[ExposeServices(typeof(IWorkflowExtension))]
public sealed class FieldValueCondition : IWorkflowExtension, ITransientDependency
{
    public string Key => "field-value";
    public string Kind => WorkflowExtensionKinds.Condition;
    public string Title => "Field value";
    public string Description => "Only when a field has, or hasn't, a value or one of some values.";

    public string SettingsSchema => """
        {"type":"object","required":["field","op"],"properties":{
          "field":{"type":"string","title":"Field","format":"field"},
          "op":{"type":"string","title":"Test","enum":["equals","notEquals","in","notIn","isEmpty","isNotEmpty"],
            "x-enumTitles":{"equals":"is","notEquals":"is not","in":"is one of","notIn":"is none of","isEmpty":"is empty","isNotEmpty":"has a value"}},
          "values":{"type":"array","title":"Values","items":{"type":"string","format":"fieldValue","x-field":"field"}},
          "message":{"type":"string","title":"Message when it doesn't hold"}}}
        """;

    public string Summary(JsonElement? settings, string language)
    {
        string field = ExtensionHelpers.Str(settings, "field") ?? "?";
        List<string> values = ExtensionHelpers.Strings(settings, "values");
        return ExtensionHelpers.Str(settings, "op") switch
        {
            "isEmpty" => $"{field} is empty",
            "isNotEmpty" => $"{field} has a value",
            "notEquals" or "notIn" => $"{field} is not {ExtensionHelpers.List(values)}",
            _ => $"{field} is {ExtensionHelpers.List(values)}",
        };
    }

    public Task<IReadOnlyList<string>> CheckSettingsAsync(JsonElement? settings, IWorkflowSiteContext site, CancellationToken cancellationToken) =>
        ExtensionHelpers.FieldsExistAsync(site, [ExtensionHelpers.Str(settings, "field") ?? ""], cancellationToken);

    public Task<string?> EvaluateAsync(WorkflowExtensionContext context, JsonElement? settings, CancellationToken cancellationToken)
    {
        string field = ExtensionHelpers.Str(settings, "field") ?? "";
        List<string> have = ExtensionHelpers.Texts(context.Values, field);
        List<string> wanted = ExtensionHelpers.Strings(settings, "values");
        bool holds = ExtensionHelpers.Str(settings, "op") switch
        {
            "isEmpty" => have.Count == 0,
            "isNotEmpty" => have.Count > 0,
            "notEquals" or "notIn" => !have.Any(wanted.Contains),
            _ => have.Any(wanted.Contains),
        };
        return Task.FromResult(holds ? null : ExtensionHelpers.Str(settings, "message") ?? $"Only when {Summary(settings, "en")}.");
    }
}

/// <summary>Holds for some people: named accounts, members of groups (nested groups count), the reporter, the assignee, the project's admins.</summary>
[ExposeServices(typeof(IWorkflowExtension))]
public sealed class PersonCondition : IWorkflowExtension, ITransientDependency
{
    public string Key => "person";
    public string Kind => WorkflowExtensionKinds.Condition;
    public string Title => "Person or group";
    public string Description => "Only for some people: accounts, groups, the reporter, the assignee or the project's admins.";

    public string SettingsSchema => """
        {"type":"object","properties":{
          "accounts":{"type":"array","title":"Accounts","items":{"type":"string","format":"account"}},
          "groups":{"type":"array","title":"Groups","items":{"type":"string","format":"group"}},
          "who":{"type":"array","title":"People on the record","items":{"type":"string","enum":["reporter","assignee","projectAdmins"],
            "x-enumTitles":{"reporter":"The reporter","assignee":"The assignee","projectAdmins":"The project's admins"}}}}}
        """;

    public string Summary(JsonElement? settings, string language)
    {
        List<string> parts = ExtensionHelpers.Strings(settings, "who").Select(w => w switch
        {
            "reporter" => "the reporter",
            "assignee" => "the assignee",
            _ => "the project's admins",
        }).ToList();
        int accounts = ExtensionHelpers.Strings(settings, "accounts").Count, groups = ExtensionHelpers.Strings(settings, "groups").Count;
        if (accounts > 0) parts.Add(accounts == 1 ? "1 named person" : $"{accounts} named people");
        if (groups > 0) parts.Add(groups == 1 ? "members of 1 group" : $"members of {groups} groups");
        return parts.Count == 0 ? "Nobody" : "Only " + ExtensionHelpers.List(parts);
    }

    public Task<IReadOnlyList<string>> CheckSettingsAsync(JsonElement? settings, IWorkflowSiteContext site, CancellationToken cancellationToken) =>
        ExtensionHelpers.Problems(
            ExtensionHelpers.Strings(settings, "accounts").Select(async a => Guid.TryParse(a, out Guid id) && await site.AccountExistsAsync(id, cancellationToken) ? null : $"There's no account {a}.")
                .Concat(ExtensionHelpers.Strings(settings, "groups").Select(async g => Guid.TryParse(g, out Guid id) && await site.GroupExistsAsync(id, cancellationToken) ? null : $"There's no group {g}.")));

    public async Task<string?> EvaluateAsync(WorkflowExtensionContext context, JsonElement? settings, CancellationToken cancellationToken)
    {
        string me = context.ActorId.ToString();
        List<string> who = ExtensionHelpers.Strings(settings, "who");
        if (ExtensionHelpers.Strings(settings, "accounts").Contains(me, StringComparer.OrdinalIgnoreCase)
            || (who.Contains("reporter") && ExtensionHelpers.Texts(context.Before, "reporter").Contains(me))
            || (who.Contains("assignee") && ExtensionHelpers.Texts(context.Before, "assignee").Contains(me))
            || (who.Contains("projectAdmins") && await context.ActorManagesScopeAsync(cancellationToken)))
        {
            return null;
        }

        List<string> groups = ExtensionHelpers.Strings(settings, "groups");
        if (groups.Count > 0 && (await context.ActorGroupIdsAsync(cancellationToken)).Any(g => groups.Contains(g.ToString(), StringComparer.OrdinalIgnoreCase)))
        {
            return null;
        }

        // The assignee's delegate counts as the assignee while the delegation lasts (ADR 0042 §3).
        if (who.Contains("assignee") && ExtensionHelpers.Texts(context.Before, "assignee") is [var assignee] && Guid.TryParse(assignee, out Guid owner)
            && await context.ActorHoldsAssignmentsOfAsync(owner, cancellationToken))
        {
            return null;
        }

        return $"{Summary(settings, "en")} can do this.";
    }
}

/// <summary>Fields that must have a value once the transition's own fields are filled in.</summary>
[ExposeServices(typeof(IWorkflowExtension))]
public sealed class RequiredCheck : IWorkflowExtension, ITransientDependency
{
    public string Key => "required";
    public string Kind => WorkflowExtensionKinds.Check;
    public string Title => "Required fields";
    public string Description => "Fields that must have a value to take the transition.";
    public string SettingsSchema => """{"type":"object","required":["fields"],"properties":{"fields":{"type":"array","title":"Fields","items":{"type":"string","format":"field"}}}}""";
    public string Summary(JsonElement? settings, string language) => $"{ExtensionHelpers.List(ExtensionHelpers.Strings(settings, "fields"))} required";

    public Task<IReadOnlyList<string>> CheckSettingsAsync(JsonElement? settings, IWorkflowSiteContext site, CancellationToken cancellationToken) =>
        ExtensionHelpers.FieldsExistAsync(site, ExtensionHelpers.Strings(settings, "fields"), cancellationToken);

    public Task<string?> EvaluateAsync(WorkflowExtensionContext context, JsonElement? settings, CancellationToken cancellationToken)
    {
        List<string> missing = ExtensionHelpers.Strings(settings, "fields").Where(f => ExtensionHelpers.Texts(context.Values, f).Count == 0).ToList();
        return Task.FromResult(missing.Count == 0 ? null : $"Fill in {ExtensionHelpers.List(missing)} first.");
    }
}

/// <summary>Fields the person must change while taking the transition.</summary>
[ExposeServices(typeof(IWorkflowExtension))]
public sealed class ChangedCheck : IWorkflowExtension, ITransientDependency
{
    public string Key => "changed";
    public string Kind => WorkflowExtensionKinds.Check;
    public string Title => "Changed fields";
    public string Description => "Fields the person must change while taking the transition.";
    public string SettingsSchema => """{"type":"object","required":["fields"],"properties":{"fields":{"type":"array","title":"Fields","items":{"type":"string","format":"field"}}}}""";
    public string Summary(JsonElement? settings, string language) => $"{ExtensionHelpers.List(ExtensionHelpers.Strings(settings, "fields"))} must change";

    public Task<IReadOnlyList<string>> CheckSettingsAsync(JsonElement? settings, IWorkflowSiteContext site, CancellationToken cancellationToken) =>
        ExtensionHelpers.FieldsExistAsync(site, ExtensionHelpers.Strings(settings, "fields"), cancellationToken);

    public Task<string?> EvaluateAsync(WorkflowExtensionContext context, JsonElement? settings, CancellationToken cancellationToken)
    {
        List<string> same = ExtensionHelpers.Strings(settings, "fields")
            .Where(f => ExtensionHelpers.Texts(context.Values, f).SequenceEqual(ExtensionHelpers.Texts(context.Before, f))).ToList();
        return Task.FromResult(same.Count == 0 ? null : $"Change {ExtensionHelpers.List(same)} to take this transition.");
    }
}

/// <summary>A text field must match a pattern (checked with a time limit). Empty values pass; combine with Required.</summary>
[ExposeServices(typeof(IWorkflowExtension))]
public sealed class PatternCheck : IWorkflowExtension, ITransientDependency
{
    public string Key => "pattern";
    public string Kind => WorkflowExtensionKinds.Check;
    public string Title => "Pattern";
    public string Description => "A text field must match a regular expression.";

    public string SettingsSchema => """
        {"type":"object","required":["field","pattern"],"properties":{
          "field":{"type":"string","title":"Field","format":"field"},
          "pattern":{"type":"string","title":"Regular expression"},
          "message":{"type":"string","title":"Message when it doesn't match"}}}
        """;

    public string Summary(JsonElement? settings, string language) => $"{ExtensionHelpers.Str(settings, "field")} matches {ExtensionHelpers.Str(settings, "pattern")}";

    public async Task<IReadOnlyList<string>> CheckSettingsAsync(JsonElement? settings, IWorkflowSiteContext site, CancellationToken cancellationToken)
    {
        List<string> problems = [.. await ExtensionHelpers.FieldsExistAsync(site, [ExtensionHelpers.Str(settings, "field") ?? ""], cancellationToken)];
        try
        {
            _ = new Regex(ExtensionHelpers.Str(settings, "pattern") ?? "", RegexOptions.None, TimeSpan.FromMilliseconds(100));
        }
        catch (ArgumentException ex)
        {
            problems.Add($"The pattern isn't a regular expression: {ex.Message}");
        }

        return problems;
    }

    public Task<string?> EvaluateAsync(WorkflowExtensionContext context, JsonElement? settings, CancellationToken cancellationToken)
    {
        string field = ExtensionHelpers.Str(settings, "field") ?? "";
        Regex regex = new(ExtensionHelpers.Str(settings, "pattern") ?? "", RegexOptions.None, TimeSpan.FromMilliseconds(100));
        foreach (string value in ExtensionHelpers.Texts(context.Values, field))
        {
            bool matches;
            try
            {
                matches = regex.IsMatch(value);
            }
            catch (RegexMatchTimeoutException)
            {
                matches = false;
            }

            if (!matches)
            {
                return Task.FromResult<string?>(ExtensionHelpers.Str(settings, "message") ?? $"{field} doesn't have the expected form.");
            }
        }

        return Task.FromResult<string?>(null);
    }
}

/// <summary>Sets a field (priority, resolution, due date, a declared field…) to a value, or clears it.</summary>
[ExposeServices(typeof(IWorkflowExtension))]
public sealed class SetFieldAction : IWorkflowExtension, ITransientDependency
{
    public string Key => "set-field";
    public string Kind => WorkflowExtensionKinds.Action;
    public string Title => "Set a field";
    public string Description => "Sets a field to a value, or clears it.";
    public string SettingsSchema => """{"type":"object","required":["field"],"properties":{"field":{"type":"string","title":"Field","format":"field"},"value":{"title":"Value (empty clears)","format":"fieldValue","x-field":"field"}}}""";

    public string Summary(JsonElement? settings, string language) =>
        settings is { ValueKind: JsonValueKind.Object } s && s.TryGetProperty("value", out JsonElement v) && v.ValueKind != JsonValueKind.Null
            ? $"Set {ExtensionHelpers.Str(settings, "field")} to {(v.ValueKind == JsonValueKind.String ? v.GetString() : v.GetRawText())}"
            : $"Clear {ExtensionHelpers.Str(settings, "field")}";

    public Task<IReadOnlyList<string>> CheckSettingsAsync(JsonElement? settings, IWorkflowSiteContext site, CancellationToken cancellationToken) =>
        ExtensionHelpers.FieldsExistAsync(site, [ExtensionHelpers.Str(settings, "field") ?? ""], cancellationToken);

    public Task ExecuteAsync(WorkflowExtensionContext context, JsonElement? settings, CancellationToken cancellationToken)
    {
        JsonElement? value = settings is { ValueKind: JsonValueKind.Object } s && s.TryGetProperty("value", out JsonElement v) && v.ValueKind != JsonValueKind.Null ? v : null;
        return context.SetAsync(ExtensionHelpers.Str(settings, "field") ?? "", value, cancellationToken);
    }
}
