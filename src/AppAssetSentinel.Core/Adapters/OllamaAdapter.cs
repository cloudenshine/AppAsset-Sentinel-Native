using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;

namespace AppAssetSentinel.Core.Adapters;

/// <summary>How confidently an adapter can act on a discovered application instance.</summary>
public enum AdapterConfidence
{
    /// <summary>The instance, its configuration and its data were all confirmed.</summary>
    Confirmed,

    /// <summary>Some evidence exists but the effective configuration could not be proven.</summary>
    Inferred,

    /// <summary>Nothing usable was found. Never treated as "no data" or "not installed".</summary>
    Unknown
}

/// <summary>What mechanism should actually move the data (AUDIT W08).</summary>
public enum RelocationMechanism
{
    /// <summary>
    /// Change the application's own supported configuration. This is the preferred path:
    /// Ollama documents OLLAMA_MODELS, so the app itself knows where its data lives.
    /// </summary>
    OfficialConfig,

    /// <summary>
    /// Filesystem-level redirection. Only used when no supported configuration exists, and
    /// only after it has been evaluated for that specific application.
    /// </summary>
    JunctionCompat,

    /// <summary>No supported mechanism. The adapter must refuse to write.</summary>
    Unsupported
}

public sealed class OllamaInstance
{
    [JsonPropertyName("found")]
    public bool Found { get; set; }

    [JsonPropertyName("executable_path")]
    public string ExecutablePath { get; set; } = string.Empty;

    [JsonPropertyName("version")]
    public string Version { get; set; } = string.Empty;

    [JsonPropertyName("service_running")]
    public bool ServiceRunning { get; set; }

    [JsonPropertyName("api_endpoint")]
    public string ApiEndpoint { get; set; } = "http://127.0.0.1:11434";

    [JsonPropertyName("confidence")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public AdapterConfidence Confidence { get; set; } = AdapterConfidence.Unknown;

    /// <summary>Value of OLLAMA_MODELS actually visible to the process, if any.</summary>
    [JsonPropertyName("configured_models_path")]
    public string ConfiguredModelsPath { get; set; } = string.Empty;

    /// <summary>Path to Ollama's internal SQLite database (%LOCALAPPDATA%\Ollama\db.sqlite), if present.</summary>
    [JsonPropertyName("settings_db_path")]
    public string SettingsDbPath { get; set; } = string.Empty;

    /// <summary>Value of 'models' stored in Ollama desktop client's internal db.sqlite settings table, if any.</summary>
    [JsonPropertyName("internal_settings_models_path")]
    public string InternalSettingsModelsPath { get; set; } = string.Empty;

    /// <summary>Directory that really holds manifests/blobs after resolving the above.</summary>
    [JsonPropertyName("effective_models_path")]
    public string EffectiveModelsPath { get; set; } = string.Empty;

    [JsonPropertyName("models_path_source")]
    public string ModelsPathSource { get; set; } = string.Empty;

    [JsonPropertyName("manifest_count")]
    public int ManifestCount { get; set; }

    [JsonPropertyName("blob_count")]
    public int BlobCount { get; set; }

    [JsonPropertyName("store_bytes")]
    public long StoreBytes { get; set; }

    /// <summary>Models the running service reports, which is the only proof it can serve them.</summary>
    [JsonPropertyName("served_models")]
    public List<string> ServedModels { get; set; } = new();

    [JsonPropertyName("notes")]
    public List<string> Notes { get; set; } = new();
}

public sealed class AdapterHealth
{
    [JsonPropertyName("reachable")]
    public bool Reachable { get; set; }

    [JsonPropertyName("model_count")]
    public int ModelCount { get; set; }

    [JsonPropertyName("models")]
    public List<string> Models { get; set; } = new();

    [JsonPropertyName("detail")]
    public string Detail { get; set; } = string.Empty;
}

/// <summary>
/// AUDIT W08: the first production adapter, deliberately limited to one application.
///
/// The audit's correction matters: the mechanism order is <b>official configuration first</b>,
/// with filesystem redirection only as an evaluated compatibility fallback. Ollama documents
/// <c>OLLAMA_MODELS</c>, so the application can be told where its data lives instead of being
/// tricked by a reparse point.
/// </summary>
public static class OllamaAdapter
{
    public const string ConfigurationVariable = "OLLAMA_MODELS";
    public const string DefaultApiEndpoint = "http://127.0.0.1:11434";

    /// <summary>
    /// Discovers the instance, its effective configuration and its data store.
    /// Read-only: this never changes configuration or moves bytes.
    /// </summary>
    public static OllamaInstance Discover(string? apiEndpoint = null)
    {
        var instance = new OllamaInstance { ApiEndpoint = apiEndpoint ?? DefaultApiEndpoint };

        // 1. Locate the executable.
        instance.ExecutablePath = FindExecutable() ?? string.Empty;
        if (!string.IsNullOrEmpty(instance.ExecutablePath))
        {
            instance.Version = ReadVersion(instance.ExecutablePath);
            instance.Notes.Add($"可执行文件: {instance.ExecutablePath}");
        }
        else
        {
            instance.Notes.Add("未在常见位置找到 ollama 可执行文件。");
        }

        // 2. Effective configuration & Application internal settings.
        // A per-user value wins over the process value, because that is what a restarted Ollama will actually read.
        string? userValue = ReadEnvironmentVariable(ConfigurationVariable, EnvironmentVariableTarget.User);
        string? processValue = Environment.GetEnvironmentVariable(ConfigurationVariable);

        if (!string.IsNullOrWhiteSpace(userValue))
        {
            instance.ConfiguredModelsPath = userValue!;
            instance.ModelsPathSource = $"用户环境变量 {ConfigurationVariable}";
        }
        else if (!string.IsNullOrWhiteSpace(processValue))
        {
            instance.ConfiguredModelsPath = processValue!;
            instance.ModelsPathSource = $"进程环境变量 {ConfigurationVariable}（未设置用户级）";
        }
        else
        {
            instance.Notes.Add($"未设置 {ConfigurationVariable}，将使用应用默认位置。");
        }

        // Check Ollama Windows desktop client internal settings database (db.sqlite)
        string defaultDb = GetDefaultDbPath();
        if (File.Exists(defaultDb))
        {
            instance.SettingsDbPath = defaultDb;
            var dbSetting = ReadAppSetting(defaultDb);
            if (dbSetting.Found && !string.IsNullOrWhiteSpace(dbSetting.ModelsPath))
            {
                instance.InternalSettingsModelsPath = dbSetting.ModelsPath!;
                instance.Notes.Add($"APP内部设置 (db.sqlite)：{instance.InternalSettingsModelsPath}");

                if (string.IsNullOrWhiteSpace(instance.ConfiguredModelsPath))
                {
                    instance.ConfiguredModelsPath = instance.InternalSettingsModelsPath;
                    instance.ModelsPathSource = "APP内部设置 (db.sqlite)";
                }
                else if (!string.Equals(instance.ConfiguredModelsPath, instance.InternalSettingsModelsPath, StringComparison.OrdinalIgnoreCase))
                {
                    instance.Notes.Add($"[注意] 环境变量 ({instance.ConfiguredModelsPath}) 与 APP内部设置 ({instance.InternalSettingsModelsPath}) 指向不同，迁移时需同步变更，否则客户端可能失效！");
                }
            }
        }

        // 3. Resolve where the data really is.
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string defaultPath = Path.Combine(profile, ".ollama", "models");

        if (!string.IsNullOrWhiteSpace(instance.ConfiguredModelsPath))
        {
            instance.EffectiveModelsPath = instance.ConfiguredModelsPath;
            if (!Directory.Exists(instance.EffectiveModelsPath))
            {
                instance.Notes.Add("配置指向的目录当前不存在，该配置可能已失效。");
            }
        }
        else if (Directory.Exists(defaultPath))
        {
            instance.EffectiveModelsPath = defaultPath;
            instance.ModelsPathSource = "应用默认位置";
        }

        // 4. Inventory the store: manifests and blobs are the real evidence of what is present.
        if (!string.IsNullOrEmpty(instance.EffectiveModelsPath) && Directory.Exists(instance.EffectiveModelsPath))
        {
            string manifests = Path.Combine(instance.EffectiveModelsPath, "manifests");
            string blobs = Path.Combine(instance.EffectiveModelsPath, "blobs");

            if (Directory.Exists(manifests))
            {
                instance.ManifestCount = Directory.EnumerateFiles(manifests, "*", SearchOption.AllDirectories).Count();
            }

            if (Directory.Exists(blobs))
            {
                foreach (var blob in Directory.EnumerateFiles(blobs))
                {
                    instance.BlobCount++;
                    try { instance.StoreBytes += new FileInfo(blob).Length; } catch { }
                }
            }
        }

        // 5. Ask the running service, which is the only proof that models can actually be served.
        var health = ProbeService(instance.ApiEndpoint);
        instance.ServiceRunning = health.Reachable;
        instance.ServedModels = health.Models;
        instance.Notes.Add(health.Detail);

        // 6. Confidence is derived, never assumed.
        bool hasExecutable = !string.IsNullOrEmpty(instance.ExecutablePath);
        bool hasStore = instance.ManifestCount > 0 || instance.BlobCount > 0;

        instance.Found = hasExecutable || hasStore || health.Reachable;

        instance.Confidence = (hasExecutable && hasStore && health.Reachable) ? AdapterConfidence.Confirmed
            : (hasExecutable || hasStore || health.Reachable) ? AdapterConfidence.Inferred
            : AdapterConfidence.Unknown;

        return instance;
    }

    /// <summary>
    /// Read-only health probe. Lists the models the service is willing to serve, which is the
    /// acceptance signal the audit asks for after a relocation.
    /// </summary>
    public static AdapterHealth ProbeService(string apiEndpoint)
    {
        var health = new AdapterHealth();

        try
        {
            using var client = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            var response = client.GetAsync($"{apiEndpoint}/api/tags").GetAwaiter().GetResult();

            if (!response.IsSuccessStatusCode)
            {
                health.Detail = $"服务可达但返回 {(int)response.StatusCode}。";
                return health;
            }

            string body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            using var doc = JsonDocument.Parse(body);

            if (doc.RootElement.TryGetProperty("models", out var models) && models.ValueKind == JsonValueKind.Array)
            {
                foreach (var m in models.EnumerateArray())
                {
                    if (m.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String)
                    {
                        health.Models.Add(name.GetString() ?? string.Empty);
                    }
                }
            }

            health.Reachable = true;
            health.ModelCount = health.Models.Count;
            health.Detail = $"服务可访问，可提供 {health.ModelCount} 个模型。";
        }
        catch (Exception ex)
        {
            health.Detail = $"服务不可访问：{ex.GetType().Name}（未运行或端口未监听）。";
        }

        return health;
    }

    /// <summary>
    /// Chooses the mechanism for this application. Official configuration is preferred because
    /// the application then genuinely knows where its data is; a Junction is a compromise that
    /// hides the truth from the application and can be broken by its own updater (AUDIT A06).
    /// </summary>
    public static (RelocationMechanism Mechanism, string Reason) ChooseMechanism(OllamaInstance instance)
    {
        if (instance.Confidence == AdapterConfidence.Unknown)
        {
            return (RelocationMechanism.Unsupported,
                "未能确认 Ollama 实例、配置或数据存储，拒绝在未知目标上执行写入。");
        }

        return (RelocationMechanism.OfficialConfig,
            $"Ollama 官方支持 {ConfigurationVariable} 环境变量与桌面客户端内部设置 (db.sqlite)，优先双向修改官方配置与APP设置，"
            + "使CLI、服务与GUI桌面端均直接指向新位置；目录联接仅作为无配置支持时的兼容手段。");
    }

    /// <summary>Default path to Ollama's desktop internal SQLite database.</summary>
    public static string GetDefaultDbPath() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ollama", "db.sqlite");

    /// <summary>
    /// Reads the 'models' path from Ollama's internal SQLite database (%LOCALAPPDATA%\Ollama\db.sqlite).
    /// </summary>
    public static (bool Found, string? ModelsPath, string Error) ReadAppSetting(string? dbPath = null)
    {
        string path = dbPath ?? GetDefaultDbPath();
        if (!File.Exists(path)) return (false, null, string.Empty);

        try
        {
            using var connection = new SqliteConnection($"Data Source={path};Mode=ReadOnly");
            connection.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT models FROM settings LIMIT 1;";
            var result = cmd.ExecuteScalar();
            if (result != null && result != DBNull.Value)
            {
                return (true, result.ToString(), string.Empty);
            }
            return (false, null, string.Empty);
        }
        catch (Exception ex)
        {
            return (false, null, ex.Message);
        }
    }

    /// <summary>
    /// Updates the 'models' path in Ollama's internal SQLite database (%LOCALAPPDATA%\Ollama\db.sqlite).
    /// Returns previous value so it can be rolled back if migration fails.
    /// </summary>
    public static (bool Success, string? PreviousValue, string Error) UpdateAppSetting(string newPath, string? dbPath = null)
    {
        string path = dbPath ?? GetDefaultDbPath();
        if (!File.Exists(path))
        {
            return (true, null, string.Empty); // No DB file present (e.g. CLI only install)
        }

        try
        {
            string? prev = null;
            using (var connection = new SqliteConnection($"Data Source={path}"))
            {
                connection.Open();
                using (var readCmd = connection.CreateCommand())
                {
                    readCmd.CommandText = "SELECT models FROM settings LIMIT 1;";
                    var res = readCmd.ExecuteScalar();
                    if (res != null && res != DBNull.Value)
                    {
                        prev = res.ToString();
                    }
                }

                using (var updateCmd = connection.CreateCommand())
                {
                    updateCmd.CommandText = "UPDATE settings SET models = $newPath;";
                    updateCmd.Parameters.AddWithValue("$newPath", newPath);
                    updateCmd.ExecuteNonQuery();
                }
            }
            return (true, prev, string.Empty);
        }
        catch (Exception ex)
        {
            return (false, null, ex.Message);
        }
    }

    /// <summary>
    /// Restores the 'models' path in Ollama's internal SQLite database to its previous value.
    /// </summary>
    public static (bool Success, string Error) RestoreAppSetting(string? previousValue, string? dbPath = null)
    {
        string path = dbPath ?? GetDefaultDbPath();
        if (!File.Exists(path) || previousValue == null) return (true, string.Empty);

        try
        {
            using var connection = new SqliteConnection($"Data Source={path}");
            connection.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "UPDATE settings SET models = $prev;";
            cmd.Parameters.AddWithValue("$prev", previousValue);
            cmd.ExecuteNonQuery();
            return (true, string.Empty);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private static string? FindExecutable()
    {
        var candidates = new List<string>
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                @"Programs\Ollama\ollama.exe")
        };

        string? onPath = Environment.GetEnvironmentVariable("PATH")
            ?.Split(Path.PathSeparator)
            .Select(dir => Path.Combine(dir, "ollama.exe"))
            .FirstOrDefault(File.Exists);

        if (!string.IsNullOrEmpty(onPath))
        {
            candidates.Insert(0, onPath);
        }

        return candidates.FirstOrDefault(File.Exists);
    }

    private static string ReadVersion(string executable)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = executable,
                Arguments = "--version",
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var proc = Process.Start(psi);
            if (proc == null)
            {
                return string.Empty;
            }

            string stdout = proc.StandardOutput.ReadToEnd().Trim();
            proc.WaitForExit(8000);

            return stdout.Split('\n').FirstOrDefault()?.Trim() ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string? ReadEnvironmentVariable(string name, EnvironmentVariableTarget target)
    {
        try
        {
            return Environment.GetEnvironmentVariable(name, target);
        }
        catch
        {
            return null;
        }
    }
}
