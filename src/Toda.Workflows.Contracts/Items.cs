namespace Toda.Workflows;

/// <summary>
/// An item a workflow moves (ADR 0089): a record, an asset, a marketplace listing. Its kind picks the host's store; its
/// type is what binds it to a workflow ("bug", a schema's type); its scope is where it lives (a project, an asset
/// schema), or none.
/// </summary>
public sealed record WorkflowItemRef(string Kind, Guid Id, string Key, string Type, Guid? ScopeId, string? ScopeKey);

/// <summary>The kinds of item Toda's own hosts move.</summary>
public static class WorkflowItemKinds
{
    public const string Record = "record";
    public const string Asset = "asset";
}

/// <summary>
/// The stored definition's format (ADR 0089): written with every definition, read as 1 when missing, refused when
/// higher than a reader knows. Once packaged, the definition JSON is a public contract.
/// </summary>
public static class WorkflowFormat
{
    public const int Current = 1;
}
