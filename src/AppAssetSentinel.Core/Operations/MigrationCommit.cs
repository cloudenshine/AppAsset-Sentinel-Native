using System.Text.Json.Serialization;
using AppAssetSentinel.Core.Migration;
using AppAssetSentinel.Core.Policy;
using AppAssetSentinel.Core.Scanner;

namespace AppAssetSentinel.Core.Operations;

/// <summary>How the source backup should be disposed of once the switch is confirmed.</summary>
public enum CommitAction
{
    /// <summary>Leave the backup in place. Space is not reclaimed.</summary>
    Retain,

    /// <summary>Delete the backup and reclaim the space, once explicitly authorized.</summary>
    Recycle,

    /// <summary>Restore the original layout, undoing a completed switch.</summary>
    Recover
}

public sealed class CommitResult
{
    [JsonPropertyName("status")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public OperationStatus Status { get; set; } = OperationStatus.Unsupported;

    [JsonPropertyName("task_id")]
    public string TaskId { get; set; } = string.Empty;

    [JsonPropertyName("action")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public CommitAction Action { get; set; }

    /// <summary>
    /// AUDIT W07: the figure comes from observed volume free space, not from summing files.
    /// <see cref="ReclaimedBytesExpected"/> is our own accounting and is reported separately so
    /// the difference is visible rather than hidden.
    /// </summary>
    [JsonPropertyName("reclaimed_bytes_observed")]
    public long ReclaimedBytesObserved { get; set; }

    [JsonPropertyName("reclaimed_bytes_expected")]
    public long ReclaimedBytesExpected { get; set; }

    [JsonPropertyName("reclaimed_formatted")]
    public string ReclaimedFormatted { get; set; } = string.Empty;

    [JsonPropertyName("measurement_notes")]
    public List<string> MeasurementNotes { get; set; } = new();

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    [JsonPropertyName("recovery")]
    public string Recovery { get; set; } = string.Empty;

    [JsonPropertyName("record")]
    public OperationRecord? Record { get; set; }
}

/// <summary>
/// AUDIT W06/W07: the Commit step.
///
/// The audit was explicit that reclaiming the source space must not be a side effect of a
/// successful switch: it is a separate, authorized action whose reported figure comes from
/// volume evidence. It was equally explicit that removing a link is not a recovery, so
/// recovering the original layout is its own action too.
/// </summary>
public static class MigrationCommit
{
    /// <summary>
    /// Disposes the source backup of a switched task. Requires explicit authorization because
    /// it is the one step that actually destroys data.
    /// </summary>
    public static CommitResult Commit(
        OperationLog log,
        string taskId,
        bool authorized)
    {
        var loaded = log.Load(taskId);

        if (!loaded.Succeeded)
        {
            return new CommitResult
            {
                Status = OperationStatus.Failed,
                TaskId = taskId,
                Action = CommitAction.Recycle,
                Message = $"无法读取操作记录：{loaded.Error}",
                Recovery = "请先修复或人工检查操作记录，不要凭猜测删除任何目录。"
            };
        }

        if (loaded.FileMissing || loaded.Record == null)
        {
            return new CommitResult
            {
                Status = OperationStatus.Failed,
                TaskId = taskId,
                Action = CommitAction.Recycle,
                Message = $"不存在任务 {taskId} 的操作记录。",
                Recovery = "不要凭缓存或记忆删除任何目录。"
            };
        }

        var record = loaded.Record;

        // Only a confirmed switch can be committed. Committing anything else would be a guess.
        if (record.State != OperationState.Switched)
        {
            return new CommitResult
            {
                Status = OperationStatus.Blocked,
                TaskId = taskId,
                Action = CommitAction.Recycle,
                Record = record,
                Message = $"任务当前状态为 {record.State}，只有 Switched 状态可以提交。",
                Recovery = "请先确认切换确实已完成。"
            };
        }

        if (!authorized)
        {
            return new CommitResult
            {
                Status = OperationStatus.Blocked,
                TaskId = taskId,
                Action = CommitAction.Recycle,
                Record = record,
                Message = "回收源备份需要单独授权；未删除任何数据。",
                Recovery = $"授权后可回收：{record.SourceBackupPath}"
            };
        }

        if (string.IsNullOrEmpty(record.SourceBackupPath) || !Directory.Exists(record.SourceBackupPath))
        {
            // Already gone, or never existed. Report success without inventing a reclaimed figure.
            record.BackupDisposition = BackupDisposition.None;
            record.State = OperationState.Committed;
            record.AddStep("commit", "源备份不存在，标记为已提交。");
            log.Save(record);

            return new CommitResult
            {
                Status = OperationStatus.Succeeded,
                TaskId = taskId,
                Action = CommitAction.Recycle,
                Record = record,
                Message = "源备份不存在，无需回收。本次未测量到空间变化。",
                Recovery = "如需确认实际占用，请查看卷的可用空间。"
            };
        }

        // Measure the volume BEFORE, so the claimed figure is evidence rather than arithmetic.
        var before = ReadVolume(record.SourceBackupPath);
        long expected = SafeDirectorySize(record.SourceBackupPath);

        try
        {
            Directory.Delete(record.SourceBackupPath, true);
        }
        catch (Exception ex)
        {
            record.AddStep("commit", $"回收源备份失败：{ex.Message}");
            log.Save(record);

            return new CommitResult
            {
                Status = OperationStatus.NeedsAttention,
                TaskId = taskId,
                Action = CommitAction.Recycle,
                Record = record,
                ReclaimedBytesExpected = expected,
                Message = $"回收源备份失败，空间未释放：{ex.Message}",
                Recovery = $"源备份仍在：{record.SourceBackupPath}；请检查占用该目录的进程后重试。"
            };
        }

        var after = ReadVolume(record.SourceBackupPath);

        record.BackupDisposition = BackupDisposition.RecycledAfterRetention;
        record.State = OperationState.Committed;
        record.AddStep("commit", $"源备份已回收：{record.SourceBackupPath}");
        log.Save(record);

        var result = new CommitResult
        {
            Status = OperationStatus.Succeeded,
            TaskId = taskId,
            Action = CommitAction.Recycle,
            Record = record,
            ReclaimedBytesExpected = expected,
            Message = "源备份已回收，锚点仍指向新位置。",
            Recovery = "若需还原，只能从新位置复制回原路径；源备份已不在。"
        };

        if (before.AvailableBytes.HasValue && after.AvailableBytes.HasValue)
        {
            // AUDIT W07: report the observed figure and name the error sources honestly.
            long observed = after.AvailableBytes.Value - before.AvailableBytes.Value;
            result.ReclaimedBytesObserved = Math.Max(0, observed);
            result.MeasurementNotes.Add($"卷 {before.Drive} 可用空间：{before.AvailableBytes:N0} → {after.AvailableBytes:N0} 字节");

            if (observed < expected)
            {
                result.MeasurementNotes.Add(
                    "观测值小于按文件统计的预期值：文件占用为簇大小的整数倍，且期间可能有其他进程写入该卷。");
            }

            result.MeasurementNotes.Add("观测值来自卷可用空间差值，不是文件大小累加，二者不会完全相等。");
        }
        else
        {
            result.MeasurementNotes.Add("volume 可用空间无法读取，未能给出观测值。");
        }

        result.ReclaimedFormatted = FormatBytes(result.ReclaimedBytesObserved > 0
            ? result.ReclaimedBytesObserved
            : expected);

        return result;
    }

    /// <summary>
    /// Restores the original layout of a switched task. AUDIT W07 is explicit that removing a
    /// link is not a recovery: the data has to come back and the anchor has to be rebuilt.
    /// </summary>
    public static CommitResult Recover(OperationLog log, string taskId, bool authorized)
    {
        var loaded = log.Load(taskId);

        if (!loaded.Succeeded || loaded.Record == null)
        {
            return new CommitResult
            {
                Status = OperationStatus.Failed,
                TaskId = taskId,
                Action = CommitAction.Recover,
                Message = loaded.FileMissing
                    ? $"不存在任务 {taskId} 的操作记录，拒绝猜测要恢复什么。"
                    : $"操作记录损坏：{loaded.Error}"
            };
        }

        var record = loaded.Record;

        if (!authorized)
        {
            return new CommitResult
            {
                Status = OperationStatus.Blocked,
                TaskId = taskId,
                Action = CommitAction.Recover,
                Record = record,
                Message = "恢复操作需要单独授权；未做任何修改。"
            };
        }

        if (record.State == OperationState.Committed &&
            record.BackupDisposition == BackupDisposition.RecycledAfterRetention)
        {
            return new CommitResult
            {
                Status = OperationStatus.Blocked,
                TaskId = taskId,
                Action = CommitAction.Recover,
                Record = record,
                Message = "源备份已被回收，无法通过本记录的备份恢复原布局。",
                Recovery = $"数据现在位于 {record.TargetPath}；如需还原请手工复制回 {record.SourcePath}。"
            };
        }

        if (string.IsNullOrEmpty(record.SourceBackupPath) || !Directory.Exists(record.SourceBackupPath))
        {
            return new CommitResult
            {
                Status = OperationStatus.Failed,
                TaskId = taskId,
                Action = CommitAction.Recover,
                Record = record,
                Message = $"源备份不存在，无法恢复：{record.SourceBackupPath}",
                Recovery = "请人工确认数据实际位置后再处理。"
            };
        }

        if (!File.Exists(record.SourcePath) && !Directory.Exists(record.SourcePath))
        {
            // The anchor is gone entirely; simply moving the backup back is correct.
            try
            {
                Directory.Move(record.SourceBackupPath, record.SourcePath);
                record.State = OperationState.FailedRecoverable;
                record.AddStep("recover", "锚点缺失，已把源备份移回原路径。");
                log.Save(record);

                return new CommitResult
                {
                    Status = OperationStatus.Recovered,
                    TaskId = taskId,
                    Action = CommitAction.Recover,
                    Record = record,
                    Message = "已恢复原布局。"
                };
            }
            catch (Exception ex)
            {
                return new CommitResult
                {
                    Status = OperationStatus.NeedsAttention,
                    TaskId = taskId,
                    Action = CommitAction.Recover,
                    Record = record,
                    Message = $"恢复失败：{ex.Message}",
                    Recovery = $"源备份仍在：{record.SourceBackupPath}"
                };
            }
        }

        // The anchor still exists. If it is a link it must be removed before restoring, and the
        // link target must not be deleted with it.
        if (FastDirectorySizer.IsReparsePoint(record.SourcePath))
        {
            if (!JunctionEngine.RemoveJunction(record.SourcePath, out var linkError))
            {
                return new CommitResult
                {
                    Status = OperationStatus.NeedsAttention,
                    TaskId = taskId,
                    Action = CommitAction.Recover,
                    Record = record,
                    Message = $"解除锚点联接失败：{linkError}",
                    Recovery = "未做其他修改；数据仍在原处。"
                };
            }
        }
        else
        {
            // A real directory now sits at the anchor: that is unmerged work, not something to
            // delete. Refuse and say so.
            return new CommitResult
            {
                Status = OperationStatus.NeedsAttention,
                TaskId = taskId,
                Action = CommitAction.Recover,
                Record = record,
                Message = $"锚点位置现在是普通目录（{record.SourcePath}），其中可能包含切换后产生的写入。",
                Recovery = $"已保留该目录与源备份（{record.SourceBackupPath}）；请人工比对后再决定。"
            };
        }

        try
        {
            Directory.Move(record.SourceBackupPath, record.SourcePath);
            record.State = OperationState.FailedRecoverable;
            record.AddStep("recover", "已解除联接并把源备份移回原路径。");
            log.Save(record);

            return new CommitResult
            {
                Status = OperationStatus.Recovered,
                TaskId = taskId,
                Action = CommitAction.Recover,
                Record = record,
                Message = "已恢复原布局：锚点再次指向本地数据。",
                Recovery = $"新位置的数据未被删除：{record.TargetPath}"
            };
        }
        catch (Exception ex)
        {
            return new CommitResult
            {
                Status = OperationStatus.NeedsAttention,
                TaskId = taskId,
                Action = CommitAction.Recover,
                Record = record,
                Message = $"把源备份移回原位失败：{ex.Message}",
                Recovery = $"锚点已解除，源备份仍在：{record.SourceBackupPath}"
            };
        }
    }

    private static (string Drive, long? AvailableBytes) ReadVolume(string path)
    {
        try
        {
            string? root = Path.GetPathRoot(Path.GetFullPath(path));
            if (string.IsNullOrEmpty(root))
            {
                return (string.Empty, null);
            }

            var drive = new DriveInfo(root);
            return (drive.Name, drive.IsReady ? drive.AvailableFreeSpace : null);
        }
        catch
        {
            return (string.Empty, null);
        }
    }

    private static long SafeDirectorySize(string path)
    {
        try
        {
            return FastDirectorySizer.CalculateDirectorySizeDetailed(path).Bytes;
        }
        catch
        {
            return 0;
        }
    }

    private static string FormatBytes(long bytes)
    {
        double mb = bytes / (1024.0 * 1024.0);
        return mb > 1024 ? $"{Math.Round(mb / 1024, 2)} GB" : $"{Math.Round(mb, 1)} MB";
    }
}
