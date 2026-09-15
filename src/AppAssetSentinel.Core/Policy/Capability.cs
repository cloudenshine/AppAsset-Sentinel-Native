namespace AppAssetSentinel.Core.Policy;

/// <summary>
/// Every write-capable capability in the product. The audit requires that
/// unimplemented or unverified writes are never reported as success.
/// </summary>
public enum Capability
{
    /// <summary>Read-only inventory, telemetry, evidence collection. Always allowed.</summary>
    ObserveReadOnly,

    /// <summary>Compare a plan against reality without touching anything.</summary>
    PlanPreview,

    /// <summary>Produce a plan for the external uninstaller without running it.</summary>
    UninstallSimulate,

    /// <summary>Run the vendor uninstaller and clean residues. Not implemented (A01).</summary>
    UninstallLive,

    /// <summary>Recursive delete of an install directory + registry key. Not implemented (A02).</summary>
    ForceClean,

    /// <summary>Copy an asset to a vault path and replace the source with a Junction.</summary>
    VaultRelocate,

    /// <summary>Remove an existing Junction link without restoring data (A12).</summary>
    JunctionUnlink,

    /// <summary>Delete a switched task's source backup and reclaim the space (A11).</summary>
    VaultCommit,

    /// <summary>Undo a completed switch and restore the original layout (A12).</summary>
    VaultRecover,

    /// <summary>Report drift between expected and observed state. Always allowed.</summary>
    DriftDiagnose,

    /// <summary>Merge drifted data back into the vault and re-link (A06).</summary>
    DriftAutoHeal,

    /// <summary>Create a Windows System Restore checkpoint.</summary>
    RestorePointCreate,

    /// <summary>Export a registry key to the backup vault.</summary>
    RegistryBackup,

    /// <summary>Re-read external rule files from disk.</summary>
    RulesReload
}

/// <summary>
/// How far a capability may currently go. The gate is deliberately conservative:
/// anything not explicitly verified is <see cref="Unsupported"/> or <see cref="Blocked"/>.
/// </summary>
public enum CapabilityState
{
    /// <summary>No code path exists. Callers must receive Unsupported.</summary>
    Unsupported = 0,

    /// <summary>Code exists but is disabled pending the audit release gates.</summary>
    Blocked = 1,

    /// <summary>Only the non-mutating variant (simulate/diagnose) may run.</summary>
    DryRunOnly = 2,

    /// <summary>Fully verified and permitted.</summary>
    Enabled = 3
}

public sealed record CapabilityDecision(
    Capability Capability,
    CapabilityState State,
    bool AllowsMutation,
    string Reason)
{
    public bool IsAllowed => State != CapabilityState.Unsupported && State != CapabilityState.Blocked;
}
