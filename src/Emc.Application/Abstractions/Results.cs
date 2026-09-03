namespace Emc.Application.Abstractions;

/// <summary>
/// Outcome of a use case. Application services return failures rather than throwing for
/// expected conditions (a denied permission, a validation failure), so that pages can render
/// them; genuine invariant breaches still throw.
/// </summary>
public sealed record OperationResult
{
    private OperationResult(bool succeeded, string? error, string? requirementId, IReadOnlyList<string> warnings)
    {
        Succeeded = succeeded;
        Error = error;
        RequirementId = requirementId;
        Warnings = warnings;
    }

    public bool Succeeded { get; }
    public string? Error { get; }

    /// <summary>Requirement ID from docs/requirements-traceability.md, when a rule was violated.</summary>
    public string? RequirementId { get; }

    /// <summary>
    /// Non-blocking advisories, e.g. a document-number gap (VCH-009) or a possible supposition
    /// phrase in a description (ITEM-003). Warnings never prevent the operation.
    /// </summary>
    public IReadOnlyList<string> Warnings { get; }

    public static OperationResult Success(params string[] warnings)
        => new(true, null, null, warnings);

    public static OperationResult Failure(string error, string? requirementId = null)
        => new(false, error, requirementId, []);
}

public sealed record OperationResult<T>
{
    private OperationResult(
        bool succeeded, T? value, string? error, string? requirementId, IReadOnlyList<string> warnings)
    {
        Succeeded = succeeded;
        Value = value;
        Error = error;
        RequirementId = requirementId;
        Warnings = warnings;
    }

    public bool Succeeded { get; }
    public T? Value { get; }
    public string? Error { get; }
    public string? RequirementId { get; }
    public IReadOnlyList<string> Warnings { get; }

    public static OperationResult<T> Success(T value, params string[] warnings)
        => new(true, value, null, null, warnings);

    public static OperationResult<T> Failure(string error, string? requirementId = null)
        => new(false, default, error, requirementId, []);
}
