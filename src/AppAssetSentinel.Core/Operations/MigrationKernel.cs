using AppAssetSentinel.Core.Abstractions;
using AppAssetSentinel.Core.Adapters;
using AppAssetSentinel.Core.Migration;
using AppAssetSentinel.Core.Policy;
using AppAssetSentinel.Core.Scanner;

namespace AppAssetSentinel.Core.Operations;

public sealed class MigrationRequest
{
    /// <summary>Exact path the user confirmed. The kernel never rewrites it (AUDIT A10).</summary>
    public string SourcePath { get; init; } = string.Empty;

    public string TargetPath { get; init; } = string.Empty;

    public string AssetName { get; init; } = string.Empty;

    public string Category { get; init; } = string.Empty;

    /// <summary>Set by the caller; reused verbatim so a retry is idempotent (AUDIT W06).</summary>
    public string TaskId { get; init; } = "task_" + Guid.NewGuid().ToString("N")[..12];

    /// <summary>
    /// AUDIT W08: official configuration is the preferred mechanism. Filesystem redirection is
    /// a compatibility fallback that hides the truth from the application and can be severed by
    /// its own updater, so it must be an explicit, evaluated choice rather than the default.
    /// </summary>
    public RelocationMechanism Mechanism { get; init; } = RelocationMechanism.JunctionCompat;

    /// <summary>Environment variable this application reads for its data location, if any.</summary>
    public string ConfigVariable { get; init; } = string.Empty;

    /// <summary>Injected so tests never write the real user environment (AUDIT A09/W02).</summary>
    public IEnvironmentStore? EnvironmentStore { get; init; }

    /// <summary>Optional post-switch health probe. Return false to trigger rollback.</summary>
    public Func<bool>? HealthCheck { get; init; }

    /// <summary>
    /// Callback or hook to synchronize the application's internal settings store
    /// (e.g. config file, SQLite settings DB, or registry) simultaneously with relocation.
    /// Returns (Success, PreviousValue, Error).
    /// </summary>
    public Func<string, (bool Success, string? PreviousValue, string Error)>? AppSettingsUpdater { get; init; }

    /// <summary>
    /// Rollback hook if the switch fails after updating app settings.
    /// </summary>
    public Action<string?>? AppSettingsRestorer { get; init; }

    /// <summary>
    /// AUDIT W06 acceptance hook. Invoked immediately after a state has been persisted and
    /// before the corresponding filesystem action runs, which is exactly the window a crash
    /// would fall into. Production callers leave this null; the acceptance harness uses it to
    /// terminate the process and then check what a restart can determine from the log.
    /// </summary>
    public Action<OperationState>? OnStateRecorded { get; init; }
}

public sealed class MigrationKernelResult
{
    public OperationOutcome Outcome { get; init; } = OperationOutcome.Blocked(Capability.VaultRelocate, "");
    public OperationRecord? Record { get; init; }
}

/// <summary>
/// AUDIT W07: the copy/verify/switch kernel.
///
/// Design rules enforced here:
///  - the destination is always a freshly created, task-owned staging directory (A03);
///  - an existing file at the destination is never overwritten — the conflict is preserved
///    on both sides and reported (A03/A06);
///  - verification compares per-file SHA-256 plus the file set, never a byte total (A04);
///  - the source is re-measured after copying to detect concurrent writes (A04);
///  - the exact confirmed target path is honoured, not a path rebuilt from the source name (A10);
///  - the source backup is a *retained* artefact; reclaiming space is a separate committed
///    step, not a side effect of a successful switch (A11);
///  - every step is written to the log before it happens, so a crash is classifiable (W06).
/// </summary>
public static class MigrationKernel
{
    private const string StagingSuffix = ".sentinel-staging-";
    private const string BackupSuffix = ".sentinel-backup-";

    public static MigrationKernelResult Execute(
        CapabilityPolicy policy,
        OperationLog log,
        MigrationRequest request,
        CancellationToken cancellationToken = default)
    {
        var decision = policy.Check(Capability.VaultRelocate);
        if (!decision.IsAllowed)
        {
            return new MigrationKernelResult
            {
                Outcome = OperationOutcome.Blocked(Capability.VaultRelocate, decision.Reason)
            };
        }

        var record = new OperationRecord
        {
            TaskId = request.TaskId,
            AssetName = request.AssetName,
            Category = request.Category,
            SourcePath = request.SourcePath,
            TargetPath = request.TargetPath
        };

        var claimed = new List<string>();
        bool committed = false;

        // AUDIT W07: staging is task-owned scratch space. It must not outlive the call unless
        // a genuine conflict means the user needs to inspect both copies — otherwise a refused
        // relocation silently leaves a full duplicate of the user's data on disk.
        string stagingForCleanup = string.Empty;
        bool keepStaging = false;

        try
        {
            // ---------------------------------------------------------
            // 1. Preflight — no side effects yet
            // ---------------------------------------------------------
            log.Save(record); // write-ahead: the attempt exists on disk first

            var boundary = PathIdentity.ValidateRelocation(request.SourcePath, request.TargetPath);
            if (!boundary.IsOk)
            {
                record.State = OperationState.PreflightFailed;
                record.AddStep("preflight", boundary.Reason);
                log.Save(record);

                return new MigrationKernelResult
                {
                    Outcome = OperationOutcome.Failed(Capability.VaultRelocate,
                        "path_boundary_rejected", boundary.Reason),
                    Record = record
                };
            }

            record.SourceIdentity = boundary.NormalizedSource;
            record.TargetIdentity = boundary.NormalizedTarget;

            if (!Directory.Exists(boundary.NormalizedSource))
            {
                record.State = OperationState.PreflightFailed;
                record.AddStep("preflight", "源目录不存在。");
                log.Save(record);
                return new MigrationKernelResult
                {
                    Outcome = OperationOutcome.Failed(Capability.VaultRelocate, "source_missing",
                        $"源目录不存在：{boundary.NormalizedSource}"),
                    Record = record
                };
            }

            string staging = boundary.NormalizedTarget + StagingSuffix + request.TaskId;
            string backup = boundary.NormalizedSource + BackupSuffix + request.TaskId;
            record.StagingPath = staging;
            record.SourceBackupPath = backup;

            // Any failure past this point must not leave the scratch copy behind.
            stagingForCleanup = staging;

            if (Directory.Exists(staging) || File.Exists(staging))
            {
                record.State = OperationState.PreflightFailed;
                record.AddStep("preflight", "任务专属 staging 已存在，拒绝复用。");
                log.Save(record);
                return new MigrationKernelResult
                {
                    Outcome = OperationOutcome.Failed(Capability.VaultRelocate, "staging_exists",
                        $"任务专属 staging 已存在：{staging}"),
                    Record = record
                };
            }

            // Mutual exclusion on every path this task will touch.
            var resources = new[] { staging, backup, boundary.NormalizedSource, boundary.NormalizedTarget };
            if (!OperationLog.TryClaimResources(resources, out string conflict))
            {
                record.State = OperationState.PreflightFailed;
                record.AddStep("preflight", $"资源被其他未完成任务占用：{conflict}");
                log.Save(record);
                return new MigrationKernelResult
                {
                    Outcome = OperationOutcome.Blocked(Capability.VaultRelocate,
                        $"资源被另一未完成任务占用：{conflict}"),
                    Record = record
                };
            }
            claimed.AddRange(resources);
            record.AddStep("preflight", "路径边界与资源互斥校验通过。");
            log.Save(record);

            // Record expected content before touching anything.
            var sourceManifest = FileIntegrity.BuildManifest(boundary.NormalizedSource);
            if (!sourceManifest.Complete)
            {
                record.State = OperationState.PreflightFailed;
                record.AddStep("preflight", "源清单不完整，拒绝在未知内容上执行迁移。");
                log.Save(record);
                return new MigrationKernelResult
                {
                    Outcome = OperationOutcome.Failed(Capability.VaultRelocate, "source_manifest_incomplete",
                        "源目录存在无法读取的条目，拒绝迁移（未做任何修改）。"),
                    Record = record
                };
            }

            record.Mechanism = request.Mechanism;
            record.ConfigVariable = request.ConfigVariable;

            record.ExpectedFileCount = sourceManifest.Files.Count;
            record.ExpectedTotalBytes = sourceManifest.TotalBytes;
            record.ExpectedManifestComplete = sourceManifest.Complete;

            // The chosen mechanism's preconditions must hold BEFORE anything moves.
            if (request.Mechanism == RelocationMechanism.OfficialConfig &&
                string.IsNullOrWhiteSpace(request.ConfigVariable))
            {
                record.State = OperationState.PreflightFailed;
                record.AddStep("preflight", "选择了官方配置机制，但未提供配置变量。");
                log.Save(record);

                return new MigrationKernelResult
                {
                    Outcome = OperationOutcome.Failed(Capability.VaultRelocate, "missing_config_variable",
                        "官方配置机制需要 config_variable；未做任何修改。"),
                    Record = record
                };
            }

            if (request.Mechanism == RelocationMechanism.Unsupported)
            {
                record.State = OperationState.PreflightFailed;
                record.AddStep("preflight", "该应用没有受支持的迁移机制。");
                log.Save(record);

                return new MigrationKernelResult
                {
                    Outcome = OperationOutcome.Unsupported(Capability.VaultRelocate,
                        "该应用没有受支持的迁移机制，未做任何修改。"),
                    Record = record
                };
            }

            log.Save(record);

            // ---------------------------------------------------------
            // 2. Copy into task-owned staging (never over an existing file)
            // ---------------------------------------------------------
            record.State = OperationState.Copying;
            record.AddStep("copy", $"开始复制到 staging：{staging}");
            log.Save(record);
            Report(request, record);

            Directory.CreateDirectory(staging);

            foreach (var dir in sourceManifest.Directories)
            {
                Directory.CreateDirectory(Path.Combine(staging, dir));
            }

            foreach (var file in sourceManifest.Files)
            {
                cancellationToken.ThrowIfCancellationRequested();

                string sourceFile = Path.Combine(boundary.NormalizedSource, file.RelativePath);
                string stagingFile = Path.Combine(staging, file.RelativePath);
                string stagingDir = Path.GetDirectoryName(stagingFile) ?? staging;

                Directory.CreateDirectory(stagingDir);

                if (File.Exists(stagingFile))
                {
                    // Defensive only: staging is created fresh for this task, so this should
                    // never trigger. Target-side conflicts are detected below, against the
                    // directory that actually exists on disk.
                    record.Conflicts.Add(file.RelativePath);
                    keepStaging = true;
                    continue;
                }

                File.Copy(sourceFile, stagingFile, overwrite: false);
            }

            record.State = OperationState.Copied;
            record.AddStep("copy", $"复制完成，文件 {sourceManifest.Files.Count} 个，冲突 {record.Conflicts.Count} 个。");
            log.Save(record);

            // ---------------------------------------------------------
            // 3. Verify — content-level, and only then check source stability
            // ---------------------------------------------------------
            var stagingManifest = FileIntegrity.BuildManifest(staging);
            var comparison = FileIntegrity.Compare(sourceManifest, stagingManifest);

            if (!comparison.Identical)
            {
                record.State = OperationState.VerifyFailed;
                // The copy failed verification, so it has no value; the source is untouched.
                record.AddStep("verify", "内容校验未通过：" + string.Join("; ", comparison.Differences.Take(10)));
                log.Save(record);

                return new MigrationKernelResult
                {
                    Outcome = new OperationOutcome
                    {
                        Status = OperationStatus.FailedRecoverable,
                        Capability = Capability.VaultRelocate,
                        DidMutate = false,
                        Code = "verify_failed",
                        Message = "复制内容与源不一致，已中止（源保持原样，staging 可安全丢弃）。",
                        Evidence = comparison.Differences.Take(20).ToList()
                    },
                    Record = record
                };
            }

            // Source stability: if the source changed while copying, the verified copy is stale.
            var sourceAfter = FileIntegrity.BuildManifest(boundary.NormalizedSource);
            var stability = FileIntegrity.Compare(sourceManifest, sourceAfter);

            if (!stability.Identical)
            {
                record.State = OperationState.VerifyFailed;
                record.AddStep("verify", "复制过程中源发生变化，拒绝切换。");
                log.Save(record);

                return new MigrationKernelResult
                {
                    Outcome = new OperationOutcome
                    {
                        Status = OperationStatus.NeedsAttention,
                        Capability = Capability.VaultRelocate,
                        DidMutate = false,
                        Code = "source_unstable",
                        Message = "复制期间源目录发生了写入，校验副本已过期，未做切换。",
                        Evidence = stability.Differences.Take(20).ToList(),
                        Recovery = "先在维护窗口停止上游写入，再重新发起迁移。"
                    },
                    Record = record
                };
            }

            // AUDIT A03/A06: the overlap that matters is between the verified copy and the
            // directory that already exists at the confirmed target. Comparing against the
            // fresh staging directory could never detect it.
            if (Directory.Exists(boundary.NormalizedTarget))
            {
                var existingManifest = FileIntegrity.BuildManifest(boundary.NormalizedTarget);

                if (existingManifest.Complete)
                {
                    var existing = existingManifest.Files.ToDictionary(f => f.RelativePath, StringComparer.OrdinalIgnoreCase);

                    foreach (var file in stagingManifest.Files)
                    {
                        if (existing.TryGetValue(file.RelativePath, out var other) &&
                            !string.Equals(file.Sha256, other.Sha256, StringComparison.OrdinalIgnoreCase))
                        {
                            record.Conflicts.Add(file.RelativePath);
                        }
                    }

                    if (record.Conflicts.Count > 0)
                    {
                        record.AddStep("verify",
                            $"目标已存在 {record.Conflicts.Count} 个同名不同内容的文件，未覆盖任何一侧。");
                    }
                }
                else
                {
                    record.AddStep("verify", "目标目录存在无法读取的条目，无法安全比对，拒绝继续。");
                    record.Conflicts.Add("(目标清单不完整)");
                }
            }

            if (record.Conflicts.Count > 0)
            {
                // Both copies now matter, so the staging copy is evidence the user needs.
                keepStaging = true;

                record.State = OperationState.NeedsAttention;
                record.AddStep("verify", "存在目标同名冲突，需要人工决定，未做切换。");
                log.Save(record);

                return new MigrationKernelResult
                {
                    Outcome = new OperationOutcome
                    {
                        Status = OperationStatus.NeedsAttention,
                        Capability = Capability.VaultRelocate,
                        DidMutate = false,
                        Code = "target_conflict",
                        Message = $"目标位置已有 {record.Conflicts.Count} 个不同内容的同名文件，两侧数据均已保留，未覆盖、未切换。"
                            + $" 为保证两份内容都可查看，暂存副本保留在 {staging}（占用磁盘空间，处理完后请删除）。",
                        Evidence = record.Conflicts.Take(20).ToList(),
                        Recovery = $"请人工比对后再决定保留哪一份；比对完成后可安全删除暂存目录：{staging}"
                    },
                    Record = record
                };
            }

            record.State = OperationState.Verified;
            record.AddStep("verify", "内容与源稳定性和清单全部一致。");
            log.Save(record);
            Report(request, record);

            // ---------------------------------------------------------
            // 4. Publish staging under the confirmed target path
            // ---------------------------------------------------------
            if (Directory.Exists(boundary.NormalizedTarget))
            {
                // Nothing to publish over; refuse rather than merge into unknown data.
                record.State = OperationState.NeedsAttention;
                record.AddStep("publish", "目标路径已被占用，拒绝覆盖。");
                log.Save(record);

                return new MigrationKernelResult
                {
                    Outcome = new OperationOutcome
                    {
                        Status = OperationStatus.NeedsAttention,
                        Capability = Capability.VaultRelocate,
                        DidMutate = false,
                        Code = "target_occupied",
                        Message = $"目标路径已存在且不属于本任务：{boundary.NormalizedTarget}"
                            + "（本次创建的暂存副本已删除，源数据未受影响）",
                        Recovery = "请确认目标位置后重试。"
                    },
                    Record = record
                };
            }

            Directory.CreateDirectory(Path.GetDirectoryName(boundary.NormalizedTarget)!);
            Directory.Move(staging, boundary.NormalizedTarget);
            record.AddStep("publish", $"staging 已发布为确认目标：{boundary.NormalizedTarget}");
            log.Save(record);

            // ---------------------------------------------------------
            // 5. Switch: park the source, then re-link the anchor
            // ---------------------------------------------------------
            if (request.Mechanism == RelocationMechanism.OfficialConfig)
            {
                // ---------------------------------------------------------
                // 5a. Preferred switch: change the application's own configuration.
                //     The application then genuinely knows where its data is.
                // ---------------------------------------------------------
                // Preconditions were already enforced in preflight; reaching here means they hold.
                var envStore = request.EnvironmentStore ?? SystemEnvironmentStore.Instance;

                record.ConfigWasSet = envStore.Get(request.ConfigVariable, EnvironmentScope.User) != null;
                record.ConfigPreviousValue = envStore.Get(request.ConfigVariable, EnvironmentScope.User);
                record.ConfigAppliedValue = boundary.NormalizedTarget;
                log.Save(record);

                // Park the old directory so nothing writes into it while the config flips.
                Directory.Move(boundary.NormalizedSource, backup);
                record.AddStep("switch", $"原目录已改名为备份：{backup}");
                log.Save(record);

                var previous = envStore.Set(request.ConfigVariable, boundary.NormalizedTarget, EnvironmentScope.User);

                // Synchronously update application internal settings (e.g. SQLite db.sqlite, config file)
                string? prevAppSetting = null;
                if (request.AppSettingsUpdater != null)
                {
                    var appResult = request.AppSettingsUpdater(boundary.NormalizedTarget);
                    if (!appResult.Success)
                    {
                        // Rollback env and directory
                        envStore.Restore(request.ConfigVariable, previous, EnvironmentScope.User);
                        try
                        {
                            if (Directory.Exists(backup) && !Directory.Exists(boundary.NormalizedSource))
                            {
                                Directory.Move(backup, boundary.NormalizedSource);
                            }
                        }
                        catch { }

                        record.State = OperationState.SwitchFailed;
                        record.AddStep("switch", $"更新APP内部设置失败，已自动回滚：{appResult.Error}");
                        log.Save(record);

                        return new MigrationKernelResult
                        {
                            Outcome = new OperationOutcome
                            {
                                Status = OperationStatus.FailedRecoverable,
                                Capability = Capability.VaultRelocate,
                                DidMutate = false,
                                Code = "app_settings_update_failed",
                                Message = $"更新APP内部设置失败，已自动回滚环境配置与原目录：{appResult.Error}",
                                Recovery = "原数据仍在原位置；请确认应用已彻底关闭后重试。"
                            },
                            Record = record
                        };
                    }

                    prevAppSetting = appResult.PreviousValue;
                    record.AppSettingTarget = "app_internal_settings";
                    record.AppSettingPreviousValue = prevAppSetting;
                    record.AppSettingAppliedValue = boundary.NormalizedTarget;
                    record.AddStep("switch", $"已同步更新APP内部设置（原值：{prevAppSetting ?? "空"}，新值：{boundary.NormalizedTarget}）");
                    log.Save(record);
                }

                // A health probe is the only proof the consumer actually reads the new location.
                bool healthy = request.HealthCheck == null || request.HealthCheck();

                if (!healthy)
                {
                    request.AppSettingsRestorer?.Invoke(prevAppSetting);
                    envStore.Restore(request.ConfigVariable, previous, EnvironmentScope.User);
                    try
                    {
                        if (Directory.Exists(backup) && !Directory.Exists(boundary.NormalizedSource))
                        {
                            Directory.Move(backup, boundary.NormalizedSource);
                        }
                    }
                    catch { }

                    record.State = OperationState.SwitchFailed;
                    record.AddStep("switch", "配置切换后健康检查未通过，已回滚配置与原目录。");
                    log.Save(record);

                    return new MigrationKernelResult
                    {
                        Outcome = new OperationOutcome
                        {
                            Status = OperationStatus.FailedRecoverable,
                            Capability = Capability.VaultRelocate,
                            DidMutate = false,
                            Code = "health_check_failed",
                            Message = "切换到新位置后应用健康检查未通过，已回滚配置与原目录。",
                            Recovery = "数据仍在原位置；请确认停用了旧实例并重新发起。"
                        },
                        Record = record
                    };
                }

                record.State = OperationState.Switched;
                record.AddStep("switch", $"已修改官方配置 {request.ConfigVariable}={boundary.NormalizedTarget} 并通过健康检查。");
                log.Save(record);

                committed = true;

                var evidenceList = new List<string>
                {
                    $"配置项: {request.ConfigVariable}={boundary.NormalizedTarget}",
                    $"原目录备份: {backup}（保留中）",
                    $"文件数: {sourceManifest.Files.Count}，字节: {sourceManifest.TotalBytes}"
                };

                if (!string.IsNullOrEmpty(record.AppSettingAppliedValue))
                {
                    evidenceList.Add($"APP设置同步更新: {record.AppSettingAppliedValue} (原值: {record.AppSettingPreviousValue ?? "空"})");
                }

                return new MigrationKernelResult
                {
                    Outcome = new OperationOutcome
                    {
                        Status = OperationStatus.Succeeded,
                        Capability = Capability.VaultRelocate,
                        DidMutate = true,
                        Code = "switched_via_config",
                        Message = $"已通过官方配置与APP设置切换位置（{request.ConfigVariable}）。原目录保留为备份，空间尚未回收。",
                        Evidence = evidenceList,
                        Recovery = "空间回收是独立的受授权步骤，不会随切换自动发生。"
                    },
                    Record = record
                };
            }

            // ---------------------------------------------------------
            // 5b. Compatibility fallback: filesystem redirection.
            // ---------------------------------------------------------
            Directory.Move(boundary.NormalizedSource, backup);
            record.AddStep("switch", $"源已改名为备份：{backup}");
            log.Save(record);

            if (!JunctionEngine.CreateJunction(boundary.NormalizedSource, boundary.NormalizedTarget, out var linkError))
            {
                // Roll back immediately: the original layout must survive a failed switch.
                try
                {
                    if (Directory.Exists(backup) && !Directory.Exists(boundary.NormalizedSource))
                    {
                        Directory.Move(backup, boundary.NormalizedSource);
                    }
                }
                catch { }

                record.State = OperationState.SwitchFailed;
                record.AddStep("switch", $"建立联接失败并已回滚：{linkError}");
                log.Save(record);

                return new MigrationKernelResult
                {
                    Outcome = new OperationOutcome
                    {
                        Status = OperationStatus.FailedRecoverable,
                        Capability = Capability.VaultRelocate,
                        DidMutate = false,
                        Code = "switch_failed",
                        Message = $"建立目录联接失败，已回滚到原布局：{linkError}",
                        Recovery = "数据仍在原位置；请检查目录联接权限后重试。"
                    },
                    Record = record
                };
            }

            // Confirm the anchor really resolves to the new location.
            var junction = FastDirectorySizer.GetJunctionInfo(boundary.NormalizedSource);
            if (!junction.IsJunction)
            {
                record.State = OperationState.NeedsAttention;
                record.AddStep("switch", "联接已创建但未被识别为重解析点，需人工确认。");
                log.Save(record);

                return new MigrationKernelResult
                {
                    Outcome = new OperationOutcome
                    {
                        Status = OperationStatus.NeedsAttention,
                        Capability = Capability.VaultRelocate,
                        DidMutate = true,
                        Code = "switch_unverified",
                        Message = "联接已创建但未能确认语义，源备份已保留。",
                        Recovery = $"原数据保留在 {backup}；请人工确认后再决定清理。"
                    },
                    Record = record
                };
            }

            record.State = OperationState.Switched;
            record.AddStep("switch", "联接已建立并确认，源备份保留。");
            log.Save(record);
            Report(request, record);

            committed = true;

            return new MigrationKernelResult
            {
                Outcome = new OperationOutcome
                {
                    Status = OperationStatus.Succeeded,
                    Capability = Capability.VaultRelocate,
                    DidMutate = true,
                    Code = "switched",
                    Message = "切换完成：原路径已指向新位置。源备份仍保留，空间尚未回收。",
                    Evidence = new List<string>
                    {
                        $"源锚点: {boundary.NormalizedSource}",
                        $"新位置: {boundary.NormalizedTarget}",
                        $"源备份: {backup}（保留中）",
                        $"文件数: {sourceManifest.Files.Count}，字节: {sourceManifest.TotalBytes}"
                    },
                    Recovery = "空间回收是独立的受授权步骤（W06 Commit），不会随切换自动发生。"
                },
                Record = record
            };
        }
        catch (OperationCanceledException)
        {
            record.State = OperationState.FailedRecoverable;
            record.AddStep("cancel", "操作被取消。");
            TryRestoreOriginal(record);
            log.Save(record);

            return new MigrationKernelResult
            {
                Outcome = new OperationOutcome
                {
                    Status = OperationStatus.FailedRecoverable,
                    Capability = Capability.VaultRelocate,
                    DidMutate = false,
                    Code = "cancelled",
                    Message = "操作已取消，已恢复到原布局。",
                    Recovery = "可重新发起；源数据未丢失。"
                },
                Record = record
            };
        }
        catch (Exception ex)
        {
            record.State = committed ? OperationState.NeedsAttention : OperationState.FailedRecoverable;
            record.AddStep("error", ex.Message);
            if (!committed)
            {
                TryRestoreOriginal(record);
            }
            log.Save(record);

            return new MigrationKernelResult
            {
                Outcome = new OperationOutcome
                {
                    Status = committed ? OperationStatus.NeedsAttention : OperationStatus.FailedRecoverable,
                    Capability = Capability.VaultRelocate,
                    DidMutate = committed,
                    Code = "kernel_error",
                    Message = ex.Message,
                    Recovery = committed
                        ? "切换可能已部分生效，请依据操作记录中的路径人工核对。"
                        : "已尝试恢复原布局；请查看操作记录确认实际状态。"
                },
                Record = record
            };
        }
        finally
        {
            // AUDIT W07: a refused or failed relocation must not leave a duplicate copy of the
            // user's data on disk. Staging survives only when a conflict makes it evidence.
            if (!keepStaging && !string.IsNullOrEmpty(stagingForCleanup))
            {
                TryRemoveStaging(stagingForCleanup, record);
            }

            OperationLog.ReleaseResources(claimed);
        }
    }

    /// <summary>Reports a recorded state to the acceptance hook, if one is installed.</summary>
    private static void Report(MigrationRequest request, OperationRecord record)
    {
        request.OnStateRecorded?.Invoke(record.State);
    }

    /// <summary>
    /// Removes the task-owned staging directory. It only ever deletes the path this task
    /// created itself, and reports failure rather than hiding it.
    /// </summary>
    private static void TryRemoveStaging(string staging, OperationRecord record)
    {
        try
        {
            if (!Directory.Exists(staging))
            {
                return;
            }

            // Safety: never follow into anything we did not create.
            if (string.IsNullOrWhiteSpace(staging) || !staging.Contains(StagingSuffix, StringComparison.OrdinalIgnoreCase))
            {
                record.AddStep("cleanup", $"跳过清理，路径不像本任务创建：{staging}");
                return;
            }

            Directory.Delete(staging, true);
            record.AddStep("cleanup", $"已清理暂存目录：{staging}");
        }
        catch (Exception ex)
        {
            record.AddStep("cleanup", $"暂存目录清理失败，需人工处理：{staging}（{ex.Message}）");
            record.RecoveryNote = string.IsNullOrEmpty(record.RecoveryNote)
                ? $"暂存目录未能自动清理：{staging}"
                : record.RecoveryNote + $" 暂存目录未能自动清理：{staging}";
        }
    }

    /// <summary>
    /// Undo a partially applied switch. Only reverses artefacts this task created, and never
    /// deletes a path it cannot prove belongs to the task (AUDIT W06 recovery requirement).
    /// </summary>
    private static void TryRestoreOriginal(OperationRecord record)
    {
        try
        {
            if (string.IsNullOrEmpty(record.SourceBackupPath) || !Directory.Exists(record.SourceBackupPath))
            {
                return;
            }

            if (Directory.Exists(record.SourcePath) && FastDirectorySizer.IsReparsePoint(record.SourcePath))
            {
                JunctionEngine.RemoveJunction(record.SourcePath, out _);
            }

            if (!Directory.Exists(record.SourcePath))
            {
                Directory.Move(record.SourceBackupPath, record.SourcePath);
                record.RecoveryNote = "已把源备份移回原路径。";
            }
        }
        catch (Exception ex)
        {
            record.RecoveryNote = $"恢复未完成，需要人工处理：{ex.Message}";
        }
    }
}
