using AppAssetSentinel.Core.Operations;
using AppAssetSentinel.Core.Policy;

namespace AppAssetSentinel.App;

/// <summary>
/// AUDIT W06 acceptance affordance: terminate this process at a chosen point inside a
/// relocation, so a real crash can be injected rather than only simulated.
///
/// W06 requires that "after a forced termination at each critical step and a restart, the
/// actual state can be determined from the log". That cannot be demonstrated in-process: the
/// test has to genuinely die. This mode exists so the acceptance run is reproducible.
///
/// Safety: it is a deliberate test hook, so it refuses to run unless the caller names an
/// explicit fixture directory created for the purpose, and it refuses any path that looks like
/// a user data location rather than a throwaway fixture.
/// </summary>
public static class FaultInjection
{
    private static readonly string[] ForbiddenFragments =
    {
        @"Windows", @"System32", @"Program Files", @"Users", @"AppData"
    };

    public static int Run(string[] args, CapabilityPolicy policy)
    {
        string? source = Value(args, "--source=");
        string? target = Value(args, "--target=");
        string? crashAt = Value(args, "--crash-at=");

        if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(target) || string.IsNullOrWhiteSpace(crashAt))
        {
            Console.Error.WriteLine("用法：--fault-inject --source=<夹具目录> --target=<确认目标> --crash-at=<Planned|Copying|Verified|Switched>");
            return 2;
        }

        // Refuse anything that is not obviously a throwaway fixture.
        foreach (var fragment in ForbiddenFragments)
        {
            if (source.Contains(fragment, StringComparison.OrdinalIgnoreCase) ||
                target.Contains(fragment, StringComparison.OrdinalIgnoreCase))
            {
                Console.Error.WriteLine($"[拒绝] 路径命中受保护片段「{fragment}」。此模式只允许用于一次性夹具目录。");
                return 3;
            }
        }

        if (!source.Contains("sentinel_fixture", StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine("[拒绝] 源路径必须包含 sentinel_fixture，以证明它是本次验收创建的夹具。");
            return 3;
        }

        var requested = crashAt.Trim();
        var log = new OperationLog(OperationLog.DefaultDirectory);

        Console.WriteLine($"[fault-inject] 目标崩溃点：{requested}");

        var result = MigrationKernel.Execute(policy, log, new MigrationRequest
        {
            SourcePath = source,
            TargetPath = target,
            AssetName = "fault-injection",
            Category = "ai_models",
            OnStateRecorded = state =>
            {
                if (!string.Equals(state.ToString(), requested, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                // Die here: the log has been persisted, the next action has not run.
                Console.WriteLine($"[fault-inject] 在 {state} 记录落盘后强制终止进程。");
                Console.Out.Flush();
                Environment.FailFast($"fault-injection at {state}");
            }
        });

        Console.WriteLine($"[fault-inject] 未触发崩溃点；内核返回 {result.Outcome.Status} / {result.Outcome.Code}");
        Console.WriteLine($"TASK_ID={result.Record?.TaskId}");
        return 0;
    }

    private static string? Value(string[] args, string prefix) =>
        args.FirstOrDefault(a => a.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))?[prefix.Length..];
}