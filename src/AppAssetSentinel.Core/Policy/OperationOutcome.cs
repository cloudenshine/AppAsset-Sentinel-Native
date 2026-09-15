using System.Text.Json.Serialization;

namespace AppAssetSentinel.Core.Policy;

/// <summary>
/// Real outcome vocabulary (AUDIT A01/A02/A24). A simulation must never be able to
/// masquerade as a live execution, and an unimplemented path must never say "success".
/// </summary>
public enum OperationStatus
{
    /// <summary>The requested mutation really happened and was verified.</summary>
    Succeeded,

    /// <summary>Nothing was mutated; a preview/dry run was produced.</summary>
    Simulated,

    /// <summary>Read-only observation completed.</summary>
    Observed,

    /// <summary>No implementation exists for this capability.</summary>
    Unsupported,

    /// <summary>Implementation exists but policy currently forbids it.</summary>
    Blocked,

    /// <summary>Executed and failed; the system is back at (or was never moved from) a known state.</summary>
    Failed,

    /// <summary>Failed but the original data is still intact and a documented recovery exists.</summary>
    FailedRecoverable,

    /// <summary>Partially applied; a human must decide before proceeding.</summary>
    NeedsAttention,

    /// <summary>Rolled back successfully to the pre-operation state.</summary>
    Recovered
}

/// <summary>
/// A single, honest result object shared by every write entry point so that the UI,
/// the API and the legacy compatibility routes cannot disagree.
/// </summary>
public sealed class OperationOutcome
{
    [JsonPropertyName("status")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public OperationStatus Status { get; init; } = OperationStatus.Unsupported;

    [JsonPropertyName("capability")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public Capability Capability { get; init; }

    /// <summary>True only for <see cref="OperationStatus.Succeeded"/>.</summary>
    [JsonPropertyName("did_mutate")]
    public bool DidMutate { get; init; }

    [JsonPropertyName("code")]
    public string Code { get; init; } = string.Empty;

    [JsonPropertyName("message")]
    public string Message { get; init; } = string.Empty;

    [JsonPropertyName("task_id")]
    public string TaskId { get; init; } = string.Empty;

    [JsonPropertyName("evidence")]
    public List<string> Evidence { get; init; } = new();

    [JsonPropertyName("recovery")]
    public string Recovery { get; init; } = string.Empty;

    public static OperationOutcome Unsupported(Capability capability, string message) => new()
    {
        Status = OperationStatus.Unsupported,
        Capability = capability,
        DidMutate = false,
        Code = "not_supported",
        Message = message
    };

    public static OperationOutcome Blocked(Capability capability, string message) => new()
    {
        Status = OperationStatus.Blocked,
        Capability = capability,
        DidMutate = false,
        Code = "blocked_by_policy",
        Message = message
    };

    public static OperationOutcome Simulated(Capability capability, string message, IEnumerable<string>? evidence = null) => new()
    {
        Status = OperationStatus.Simulated,
        Capability = capability,
        DidMutate = false,
        Code = "simulated",
        Message = message,
        Evidence = evidence?.ToList() ?? new List<string>()
    };

    public static OperationOutcome Failed(Capability capability, string code, string message, string recovery = "") => new()
    {
        Status = OperationStatus.Failed,
        Capability = capability,
        DidMutate = false,
        Code = code,
        Message = message,
        Recovery = recovery
    };
}
