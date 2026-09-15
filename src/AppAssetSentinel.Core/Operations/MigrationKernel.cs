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

            record.ExpectedFileCount = sourceManifest.Files.Count;
            record.ExpectedTotalBytes = sourceManifest.TotalBytes;
            record.ExpectedManifestComplete = sourceManifest.Complete;
            log.Save(record);

            // ---------------------------------------------------------
            // 2. Copy into task-owned staging (never over an existing file)
            // ---------------------------------------------------------
            record.State = OperationState.Copying;
            record.AddStep("copy", $"开始复制到 staging：{staging}");
            log.Save(record);

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
                    // Preserve both sides rather than overwriting either (AUDIT A03/A06).
                    record.Conflicts.Add(file.RelativePath);
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

            if (record.Conflicts.Count > 0)
            {
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
                        Message = $"目标位置已有 {record.Conflicts.Count} 个不同内容的同名文件，两侧数据均已保留，未覆盖、未切换。",
                        Evidence = record.Conflicts.Take(20).ToList(),
                        Recovery = "请人工比对后再决定保留哪一份；staging 目录已保留供检查。"
                    },
                    Record = record
                };
            }

            record.State = OperationState.Verified;
            record.AddStep("verify", "内容与源稳定性和清单全部一致。");
            log.Save(record);

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
                        Message = $"目标路径已存在且不属于本任务：{boundary.NormalizedTarget}",
                        Recovery = "请确认目标位置后重试；staging 已保留。"
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
            OperationLog.ReleaseResources(claimed);
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
