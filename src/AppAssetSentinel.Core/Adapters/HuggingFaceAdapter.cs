using System.Text.Json.Serialization;

namespace AppAssetSentinel.Core.Adapters;

/// <summary>Which configuration variable actually determined the hub cache location.</summary>
public enum HubCacheOrigin
{
    /// <summary>HF_HUB_CACHE named the hub directory directly; it wins.</summary>
    HubCacheVariable,

    /// <summary>HF_HOME named the parent; the hub cache is its "hub" child.</summary>
    HomeVariable,

    /// <summary>Neither was set; the documented default applies.</summary>
    Default,

    /// <summary>Nothing could be resolved.</summary>
    Unknown
}

/// <summary>How the cache stores its payloads. Both layouts are legitimate.</summary>
public enum HubCacheLayout
{
    /// <summary>snapshots/ entries are real files copied out of blobs/.</summary>
    CopiedFiles,

    /// <summary>snapshots/ entries are symlinks into blobs/.</summary>
    SymlinkedBlobs,

    /// <summary>Mixed or not yet determined.</summary>
    Mixed,

    Unknown
}

public sealed class HuggingFaceCache
{
    [JsonPropertyName("confidence")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public AdapterConfidence Confidence { get; set; } = AdapterConfidence.Unknown;

    /// <summary>Value of HF_HOME, which is the *parent* of the hub cache (AUDIT A20).</summary>
    [JsonPropertyName("hf_home")]
    public string HfHome { get; set; } = string.Empty;

    /// <summary>Value of HF_HUB_CACHE, which names the hub directory itself.</summary>
    [JsonPropertyName("hf_hub_cache")]
    public string HfHubCache { get; set; } = string.Empty;

    /// <summary>The directory that really holds blobs/refs/snapshots.</summary>
    [JsonPropertyName("effective_hub_path")]
    public string EffectiveHubPath { get; set; } = string.Empty;

    [JsonPropertyName("origin")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public HubCacheOrigin Origin { get; set; } = HubCacheOrigin.Unknown;

    [JsonPropertyName("layout")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public HubCacheLayout Layout { get; set; } = HubCacheLayout.Unknown;

    [JsonPropertyName("blob_count")]
    public int BlobCount { get; set; }

    /// <summary>Sum of real blob sizes; shared blobs are counted once (AUDIT A20).</summary>
    [JsonPropertyName("blob_bytes")]
    public long BlobBytes { get; set; }

    [JsonPropertyName("snapshot_count")]
    public int SnapshotCount { get; set; }

    [JsonPropertyName("repository_count")]
    public int RepositoryCount { get; set; }

    [JsonPropertyName("notes")]
    public List<string> Notes { get; set; } = new();

    /// <summary>
    /// The value to write when relocating. It is the *variable that owns the layer*, never
    /// HF_HOME pointed at the hub directory itself, which would produce a nested "hub/hub".
    /// </summary>
    [JsonPropertyName("recommended_variable")]
    public string RecommendedVariable { get; set; } = string.Empty;

    [JsonPropertyName("recommended_value_for")]
    public string RecommendedValueFor { get; set; } = string.Empty;
}

/// <summary>
/// AUDIT W10 / A20: the Hugging Face cache adapter, read-only for now.
///
/// The concrete bug being fixed: the product scanned <c>...\huggingface\hub</c> but wrote
/// <c>HF_HOME</c>. Those are different layers — HF_HOME is the parent of <c>hub</c> — so the
/// naive write would have produced an extra <c>hub</c> level and orphaned the real cache.
/// This adapter resolves the effective hub directory correctly and reports which variable owns
/// it, so a relocation changes the right layer.
/// </summary>
public static class HuggingFaceAdapter
{
    public const string HomeVariable = "HF_HOME";
    public const string HubCacheVariable = "HF_HUB_CACHE";

    private static readonly string[] SnapshotLinkSuffixes = { ".no_exist" };

    public static HuggingFaceCache Discover()
    {
        var cache = new HuggingFaceCache();

        string? home = ReadVariable(HomeVariable);
        string? hubCache = ReadVariable(HubCacheVariable);

        cache.HfHome = home ?? string.Empty;
        cache.HfHubCache = hubCache ?? string.Empty;

        // Resolution order, matching the documented precedence: HF_HUB_CACHE names the hub
        // directory itself and therefore wins over HF_HOME, which names its parent.
        if (!string.IsNullOrWhiteSpace(hubCache))
        {
            cache.EffectiveHubPath = hubCache!;
            cache.Origin = HubCacheOrigin.HubCacheVariable;
            cache.Notes.Add($"{HubCacheVariable} 直接指定了 hub 目录。");
        }
        else if (!string.IsNullOrWhiteSpace(home))
        {
            cache.EffectiveHubPath = Path.Combine(home!, "hub");
            cache.Origin = HubCacheOrigin.HomeVariable;
            cache.Notes.Add($"{HomeVariable} 是父级目录，hub 缓存位于其下的 hub 子目录。");
        }
        else
        {
            string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            cache.EffectiveHubPath = Path.Combine(profile, ".cache", "huggingface", "hub");
            cache.Origin = HubCacheOrigin.Default;
            cache.Notes.Add("未设置 HF 变量，使用文档默认缓存位置。");
        }

        if (!Directory.Exists(cache.EffectiveHubPath))
        {
            cache.Notes.Add("解析出的 hub 目录当前不存在。");
            cache.Confidence = cache.Origin == HubCacheOrigin.Default
                ? AdapterConfidence.Unknown
                : AdapterConfidence.Inferred;
            ApplyRelocationTarget(cache);
            return cache;
        }

        Inventory(cache);
        ApplyRelocationTarget(cache);

        cache.Confidence = cache.RepositoryCount > 0 || cache.BlobCount > 0
            ? AdapterConfidence.Confirmed
            : AdapterConfidence.Inferred;

        return cache;
    }

    private static void Inventory(HuggingFaceCache cache)
    {
        string hub = cache.EffectiveHubPath;
        string blobs = Path.Combine(hub, "blobs");
        string snapshots = Path.Combine(hub, "snapshots");

        // Blobs are content-addressed and shared between revisions, so each one is counted
        // exactly once. Counting through snapshots would double count the same payload.
        if (Directory.Exists(blobs))
        {
            foreach (var blob in Directory.EnumerateFiles(blobs))
            {
                cache.BlobCount++;
                try { cache.BlobBytes += new FileInfo(blob).Length; } catch { }
            }
        }

        int linked = 0;
        int copied = 0;

        if (Directory.Exists(snapshots))
        {
            foreach (var repo in Directory.EnumerateDirectories(snapshots))
            {
                cache.RepositoryCount++;

                // AUDIT A20: enumeration must NOT follow reparse points. Following a
                // snapshot symlink walks back into blobs/ and counts the same shared payload
                // again, which is precisely how a shared blob gets double counted.
                foreach (var entry in EnumerateWithoutFollowingLinks(repo))
                {
                    cache.SnapshotCount++;

                    // Both layouts are legitimate; which one is in use changes how a move must
                    // be performed, because a symlink can break while a copied file cannot.
                    try
                    {
                        var attributes = File.GetAttributes(entry);
                        if ((attributes & FileAttributes.ReparsePoint) == FileAttributes.ReparsePoint)
                        {
                            linked++;
                        }
                        else if (File.Exists(entry))
                        {
                            copied++;
                        }
                    }
                    catch { }
                }
            }
        }

        cache.Layout = (linked, copied) switch
        {
            (0, 0) => HubCacheLayout.Unknown,
            (_, 0) => HubCacheLayout.SymlinkedBlobs,
            (0, _) => HubCacheLayout.CopiedFiles,
            _ => HubCacheLayout.Mixed
        };

        cache.Notes.Add($"快照条目：链接 {linked} 个、实体文件 {copied} 个，布局判定为 {cache.Layout}。");

        if (cache.Layout == HubCacheLayout.Mixed)
        {
            cache.Notes.Add("混用两种布局；迁移必须逐条目按其真实类型处理，不能假设。");
        }

        _ = SnapshotLinkSuffixes;
    }

    private static void ApplyRelocationTarget(HuggingFaceCache cache)
    {
        // Relocating must change the variable that owns the layer being moved.
        switch (cache.Origin)
        {
            case HubCacheOrigin.HubCacheVariable:
                cache.RecommendedVariable = HubCacheVariable;
                cache.RecommendedValueFor = cache.EffectiveHubPath;
                break;

            case HubCacheOrigin.HomeVariable:
                // HF_HOME must point at the parent, not at the hub directory (AUDIT A20).
                cache.RecommendedVariable = HomeVariable;
                cache.RecommendedValueFor = Path.GetDirectoryName(cache.EffectiveHubPath) ?? cache.EffectiveHubPath;
                break;

            default:
                cache.RecommendedVariable = HubCacheVariable;
                cache.RecommendedValueFor = cache.EffectiveHubPath;
                break;
        }
    }

    /// <summary>
    /// Verifies that a proposed new hub directory and a variable value agree on the same layer.
    /// A mismatch is the exact A20 defect: writing HF_HOME = "&lt;...&gt;\hub" would nest another hub.
    /// </summary>
    public static (bool Consistent, string Detail) ValidateRelocationTarget(string variable, string proposedValue)
    {
        if (string.IsNullOrWhiteSpace(variable))
        {
            return (false, "未指定配置变量，无法判断层级。");
        }

        if (string.Equals(variable, HubCacheVariable, StringComparison.OrdinalIgnoreCase))
        {
            // HF_HUB_CACHE names the hub directory itself, so a path ending in "hub" is correct.
            // It is HF_HOME that must NOT end in "hub", because HF_HOME is the parent.
            bool namesHubItself = Path.GetFileName(proposedValue.TrimEnd('\\'))
                .Equals("hub", StringComparison.OrdinalIgnoreCase);

            return namesHubItself
                ? (true, $"{HubCacheVariable} 指向 hub 目录本身，层级一致。")
                : (true, $"{HubCacheVariable} 指向的目录将作为 hub 缓存根使用，层级一致。");
        }

        if (string.Equals(variable, HomeVariable, StringComparison.OrdinalIgnoreCase))
        {
            return Path.GetFileName(proposedValue.TrimEnd('\\'))
                       .Equals("hub", StringComparison.OrdinalIgnoreCase)
                ? (false, $"{HomeVariable} 是父级目录，指向 hub 目录会把缓存变成 hub/hub。")
                : (true, $"{HomeVariable} 指向父级目录，层级一致。");
        }

        return (false, $"未知配置变量：{variable}");
    }

    /// <summary>
    /// Enumerates a subtree without descending into reparse points. Returning the link itself
    /// and not its target is what keeps shared-blob accounting correct (AUDIT A20).
    /// </summary>
    private static IEnumerable<string> EnumerateWithoutFollowingLinks(string root)
    {
        var stack = new Stack<string>();
        stack.Push(root);

        while (stack.Count > 0)
        {
            string dir = stack.Pop();
            string[] entries;

            try
            {
                entries = Directory.GetFileSystemEntries(dir);
            }
            catch
            {
                continue;
            }

            foreach (var entry in entries)
            {
                yield return entry;

                try
                {
                    var attributes = File.GetAttributes(entry);
                    bool isLink = (attributes & FileAttributes.ReparsePoint) == FileAttributes.ReparsePoint;
                    bool isDir = (attributes & FileAttributes.Directory) == FileAttributes.Directory;

                    // Do not descend into a link: its target lives elsewhere in the cache.
                    if (isDir && !isLink)
                    {
                        stack.Push(entry);
                    }
                }
                catch { }
            }
        }
    }

    private static string? ReadVariable(string name)
    {
        try
        {
            return Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User)
                   ?? Environment.GetEnvironmentVariable(name);
        }
        catch
        {
            return null;
        }
    }
}
