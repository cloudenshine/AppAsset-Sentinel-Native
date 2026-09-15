using System.Text.Json.Serialization;

namespace AppAssetSentinel.Core.Adapters;

/// <summary>What a domain can currently be asked to do.</summary>
public enum AdapterWriteCapability
{
    /// <summary>Read-only discovery only. Writes must be refused, not attempted.</summary>
    Unsupported = 0,

    /// <summary>Discovery plus a verified relocation protocol.</summary>
    Relocation = 1
}

/// <summary>
/// The shape of the payload. A single file and a directory are not interchangeable, and
/// AUDIT A21 is exactly that mistake: the Docker candidate is <c>ext4.vhdx</c> (a file) while
/// the migration backend requires <c>Directory.Exists</c>, so the candidate could be offered
/// for an operation that can never succeed.
/// </summary>
public enum AdapterPayloadKind
{
    Directory,

    /// <summary>Single file. Must never be routed to the directory relocation kernel.</summary>
    SingleFile,
    Unknown
}

public sealed class AdapterDescriptor
{
    [JsonPropertyName("domain")]
    public string Domain { get; set; } = string.Empty;

    [JsonPropertyName("display_name")]
    public string DisplayName { get; set; } = string.Empty;

    [JsonPropertyName("payload_kind")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public AdapterPayloadKind PayloadKind { get; set; } = AdapterPayloadKind.Unknown;

    [JsonPropertyName("write_capability")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public AdapterWriteCapability WriteCapability { get; set; } = AdapterWriteCapability.Unsupported;

    [JsonPropertyName("read_only_reason")]
    public string ReadOnlyReason { get; set; } = string.Empty;

    /// <summary>True only when this descriptor may be handed to the directory migration kernel.</summary>
    [JsonPropertyName("eligible_for_directory_migration")]
    public bool EligibleForDirectoryMigration { get; set; }
}

/// <summary>
/// AUDIT W10: after the first adapter, extend discovery to the remaining domains — but
/// read-only. Each domain declares what it can actually do instead of being wired into a
/// generic "move any folder" path, because a Docker volume, a chat database and a video
/// project have completely different consistency requirements.
/// </summary>
public static class AdapterRegistry
{
    public static List<AdapterDescriptor> Describe()
    {
        return new List<AdapterDescriptor>
        {
            new()
            {
                Domain = "ai_models",
                DisplayName = "AI 大模型权重",
                PayloadKind = AdapterPayloadKind.Directory,
                WriteCapability = AdapterWriteCapability.Relocation,
                ReadOnlyReason = "W08：Ollama 适配器已实现官方配置优先的迁移协议；其他 AI 工具的停放窗口尚未单独验收。",
                EligibleForDirectoryMigration = true
            },
            new()
            {
                Domain = "huggingface_cache",
                DisplayName = "HuggingFace 缓存",
                PayloadKind = AdapterPayloadKind.Directory,
                WriteCapability = AdapterWriteCapability.Unsupported,
                ReadOnlyReason = "W10：层级解析（HF_HOME 与 HF_HUB_CACHE）与布局识别已实现，但写入路径未验收。",
                EligibleForDirectoryMigration = false
            },
            new()
            {
                Domain = "docker_disk",
                DisplayName = "Docker / WSL 虚拟磁盘",
                PayloadKind = AdapterPayloadKind.SingleFile,
                WriteCapability = AdapterWriteCapability.Unsupported,
                ReadOnlyReason = "A21：ext4.vhdx 是文件而非目录，且需要先停机与一致性校验；不得送入目录迁移内核。",
                EligibleForDirectoryMigration = false
            },
            new()
            {
                Domain = "social_docs",
                DisplayName = "社交通讯归档",
                PayloadKind = AdapterPayloadKind.Directory,
                WriteCapability = AdapterWriteCapability.Unsupported,
                ReadOnlyReason = "A21：聊天数据库在应用运行时处于活动状态，需要专用停机与备份协议。",
                EligibleForDirectoryMigration = false
            },
            new()
            {
                Domain = "creative_media",
                DisplayName = "影视工程与渲染缓存",
                PayloadKind = AdapterPayloadKind.Directory,
                WriteCapability = AdapterWriteCapability.Unsupported,
                ReadOnlyReason = "A21：活动工程可能正在写入，需要应用级停放与草稿库一致性校验。",
                EligibleForDirectoryMigration = false
            }
        };
    }

    public static AdapterDescriptor? Find(string domain) =>
        Describe().FirstOrDefault(d => string.Equals(d.Domain, domain, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Decides whether a discovered candidate may be handed to the directory migration kernel.
    /// A single-file payload is rejected here rather than failing later inside the kernel.
    /// </summary>
    public static (bool Eligible, string Reason) EvaluateMigrationEligibility(string domain, string candidatePath)
    {
        var descriptor = Find(domain);
        if (descriptor == null)
        {
            return (false, $"未注册的领域「{domain}」，拒绝在未知语义上执行迁移。");
        }

        // Trust the filesystem over the declaration: a path that is actually a file can never
        // be a directory migration, whatever the registry says.
        bool isFile = File.Exists(candidatePath);
        bool isDirectory = Directory.Exists(candidatePath);

        if (isFile && !isDirectory)
        {
            return (false,
                $"候选是单个文件而非目录（{Path.GetFileName(candidatePath)}）；目录迁移内核无法处理，按 A21 拒绝。");
        }

        if (descriptor.PayloadKind == AdapterPayloadKind.SingleFile)
        {
            return (false, descriptor.ReadOnlyReason);
        }

        if (!descriptor.EligibleForDirectoryMigration)
        {
            return (false, descriptor.ReadOnlyReason);
        }

        return (true, "该领域已通过迁移协议验收。");
    }
}
