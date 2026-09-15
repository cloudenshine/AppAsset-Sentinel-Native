using System.Text.Json.Serialization;
using AppAssetSentinel.Core.Scanner;

namespace AppAssetSentinel.Core.Operations;

/// <summary>What the filesystem actually shows for a task, independent of the log.</summary>
public enum ObservedLayout
{
    /// <summary>Source holds the data and no target exists: nothing happened.</summary>
    OriginalIntact,

    /// <summary>Source is a link to a populated target, and the backup still exists.</summary>
    SwitchedWithBackup,

    /// <summary>Source is a link to a populated target and the backup is gone.</summary>
    SwitchedCommitted,

    /// <summary>Both the original directory and a populated target exist.</summary>
    BothPresent,

    /// <summary>Neither the original data nor a usable target could be found.</summary>
    DataMissing,

    /// <summary>The layout does not match anything this kernel produces.</summary>
    Unrecognised
}

/// <summary>
/// AUDIT W06: after an abrupt termination the log alone is not proof - the disk is. This
/// compares the recorded intent with what is actually on the filesystem and reports the true
/// state, so a restart never resumes from a cached "Completed" or assumes a step succeeded.
/// </summary>
public sealed class RecoveryAssessment
{
    [JsonPropertyName("task_id")]
    public string TaskId { get; set; } = string.Empty;

    [JsonPropertyName("logged_state")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public OperationState LoggedState { get; set; }

    [JsonPropertyName("observed_layout")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public ObservedLayout ObservedLayout { get; set; }

    [JsonPropertyName("log_matches_disk")]
    public bool LogMatchesDisk { get; set; }

    /// <summary>
    /// AUDIT W06: a logged state is a status, not a layout. NeedsAttention is emitted by several
    /// paths whose disk layouts differ, so for those the log alone cannot determine the state and
    /// the disk observation is authoritative. This is false in exactly those cases.
    /// </summary>
    [JsonPropertyName("log_decides")]
    public bool LogDecides { get; set; }

    [JsonPropertyName("conclusion")]
    public string Conclusion { get; set; } = string.Empty;

    [JsonPropertyName("safe_next_actions")]
    public List<string> SafeNextActions { get; set; } = new();

    [JsonPropertyName("observed_facts")]
    public List<string> ObservedFacts { get; set; } = new();

    [JsonPropertyName("data_accounted_for")]
    public bool DataAccountedFor { get; set; }
}

public static class RecoveryInspector
{
    /// <summary>
    /// Reconstructs the true state of a task by inspecting the filesystem at the paths the log
    /// recorded. Performs no writes.
    /// </summary>
    public static RecoveryAssessment Inspect(OperationLog log, string taskId)
    {
        var assessment = new RecoveryAssessment { TaskId = taskId };
        var loaded = log.Load(taskId);

        if (!loaded.Succeeded)
        {
            assessment.ObservedLayout = ObservedLayout.Unrecognised;
            assessment.Conclusion = $"操作记录损坏（{loaded.Error}），无法据此判断任何路径，已停止推断。";
            assessment.SafeNextActions.Add("人工检查记录文件；在确认实际位置前不要删除任何目录。");
            return assessment;
        }

        if (loaded.FileMissing || loaded.Record == null)
        {
            assessment.ObservedLayout = ObservedLayout.Unrecognised;
            assessment.Conclusion = "不存在该任务的记录，拒绝猜测曾有过的布局。";
            assessment.SafeNextActions.Add("如确有遗留目录，请人工确认其归属后再处理。");
            return assessment;
        }

        var record = loaded.Record;
        assessment.LoggedState = record.State;

        bool sourceIsLink = Directory.Exists(record.SourcePath) && FastDirectorySizer.IsReparsePoint(record.SourcePath);
        bool sourceIsRealDir = Directory.Exists(record.SourcePath) && !sourceIsLink;
        bool backupExists = !string.IsNullOrEmpty(record.SourceBackupPath) && Directory.Exists(record.SourceBackupPath);
        bool targetPopulated = !string.IsNullOrEmpty(record.TargetPath) && Directory.Exists(record.TargetPath);

        assessment.ObservedFacts.Add($"源路径存在={Directory.Exists(record.SourcePath)}，是链接={sourceIsLink}");
        assessment.ObservedFacts.Add($"源备份存在={backupExists}（{record.SourceBackupPath}）");
        assessment.ObservedFacts.Add($"目标路径存在={targetPopulated}（{record.TargetPath}）");

        assessment.ObservedLayout =
            sourceIsLink && targetPopulated && backupExists ? ObservedLayout.SwitchedWithBackup
            : sourceIsLink && targetPopulated && !backupExists ? ObservedLayout.SwitchedCommitted
            : sourceIsRealDir && targetPopulated ? ObservedLayout.BothPresent
            : sourceIsRealDir && !targetPopulated ? ObservedLayout.OriginalIntact
            : !Directory.Exists(record.SourcePath) && !backupExists && !targetPopulated ? ObservedLayout.DataMissing
            : ObservedLayout.Unrecognised;

        assessment.DataAccountedFor = sourceIsRealDir || backupExists || (sourceIsLink && targetPopulated);
        var expected = ExpectedLayout(record.State);

        if (expected.HasValue)
        {
            assessment.LogDecides = true;
            assessment.LogMatchesDisk = expected.Value == assessment.ObservedLayout;
        }
        else
        {
            // Ambiguous state: report the observation and refuse to claim the log is decisive.
            assessment.LogDecides = false;
            assessment.LogMatchesDisk = false;
        }

        assessment.Conclusion = !assessment.LogDecides
            ? $"记录状态为 {record.State}，该状态可能由多条路径产生，日志本身不能决定磁盘布局。"
              + $"磁盘实测为 {assessment.ObservedLayout}，以磁盘为准。"
            : assessment.LogMatchesDisk
                ? $"记录状态 {record.State} 与磁盘布局一致，可以据此继续。"
                : $"记录显示 {record.State}，但磁盘实际为 {assessment.ObservedLayout}——"
                  + "说明进程在记录某一步之后、执行该步之前被终止。以磁盘为准。";

        assessment.SafeNextActions = SuggestActions(record, assessment);
        return assessment;
    }

    /// <summary>
    /// The layout a state corresponds to, or null when the state is reachable from more than one
    /// path and therefore does not determine a layout on its own.
    /// </summary>
    private static ObservedLayout? ExpectedLayout(OperationState state) => state switch
    {
        OperationState.Planned or OperationState.PreflightFailed or OperationState.Copying
            or OperationState.Copied or OperationState.VerifyFailed or OperationState.Verified
            or OperationState.FailedRecoverable => ObservedLayout.OriginalIntact,

        // Reachable from a confirmed switch AND from "switch unverified", but also from the
        // conflict and target-occupied paths, which leave the source as a real directory. Not
        // single-valued, so the log is not treated as decisive here.
        OperationState.NeedsAttention => null,

        OperationState.Switched => ObservedLayout.SwitchedWithBackup,
        OperationState.Committed => ObservedLayout.SwitchedCommitted,
        _ => null
    };

    private static List<string> SuggestActions(OperationRecord record, RecoveryAssessment assessment)
    {
        var actions = new List<string>();

        switch (assessment.ObservedLayout)
        {
            case ObservedLayout.OriginalIntact:
                actions.Add("源数据完好，可以安全地重新发起迁移，或直接放弃本次任务。");
                if (Directory.Exists(record.StagingPath))
                {
                    actions.Add($"任务暂存目录仍存在，可安全删除：{record.StagingPath}");
                }
                break;

            case ObservedLayout.SwitchedWithBackup:
                actions.Add($"切换已完成，源备份仍在：{record.SourceBackupPath}。");
                actions.Add("确认应用可正常使用后，再通过受授权的 Commit 回收空间。");
                actions.Add("如需回到原布局，通过 Recover 执行，不要手工删除链接。");
                break;

            case ObservedLayout.SwitchedCommitted:
                actions.Add($"切换已完成且源备份已回收，数据位于：{record.TargetPath}。");
                actions.Add("不再有本地备份可回退；如需还原只能从目标位置复制回源路径。");
                break;

            case ObservedLayout.BothPresent:
                actions.Add("源路径与目标路径都是真实目录，可能各自含有不同内容。");
                actions.Add("两侧内容都必须保留；请人工比对后再决定，系统不会自动合并或删除。");
                break;

            case ObservedLayout.DataMissing:
                actions.Add("源、备份与目标均不可访问，无法确认数据位置。");
                actions.Add("请先检查相关卷是否离线，不要执行任何清理操作。");
                break;

            default:
                actions.Add("磁盘布局与任何已知状态都不匹配，已停止自动推断。");
                actions.Add("请人工检查记录中列出的三个路径。");
                break;
        }

        return actions;
    }
}