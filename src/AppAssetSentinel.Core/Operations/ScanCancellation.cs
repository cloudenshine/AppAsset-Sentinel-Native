namespace AppAssetSentinel.Core.Operations;

/// <summary>
/// AUDIT W11: a long scan must be stoppable. Cancellation here is cooperative and the caller is
/// told so rather than being promised an instant halt: the scan checks this token between major
/// phases and inside the per-asset size loop, so it stops at the next checkpoint. Anything the
/// scan had already published remains valid, because scanning never writes user data.
/// </summary>
public sealed class ScanCancellation : IDisposable
{
    private readonly CancellationTokenSource _source = new();

    public CancellationToken Token => _source.Token;

    public bool IsCancellationRequested => _source.IsCancellationRequested;

    /// <summary>Requests cancellation. Safe to call repeatedly.</summary>
    public bool Request()
    {
        if (_source.IsCancellationRequested)
        {
            return false;
        }

        _source.Cancel();
        return true;
    }

    /// <summary>Throws if cancellation was requested; used at each checkpoint.</summary>
    public void ThrowIfCancelled() => _source.Token.ThrowIfCancellationRequested();

    /// <summary>True when cancellation was requested and the operation actually stopped.</summary>
    public static bool IsCancelledResult(Exception ex) => ex is OperationCanceledException;

    /// <summary>Detail text a caller can show, stating the cooperative nature plainly.</summary>
    public static string DescribeCooperativeStop(string phase) =>
        $"已在「{phase}」阶段停止。取消是协作式的：扫描在下一次检查点结束，"
        + "已扫描完成的部分保留，未扫描的部分为空，不会被当作「没有内容」。";

    public void Dispose() => _source.Dispose();
}

/// <summary>Thrown when a size measurement was cut short by cancellation.</summary>
public sealed class ScanCancelledException : Exception
{
    public ScanCancelledException(string message) : base(message) { }
}