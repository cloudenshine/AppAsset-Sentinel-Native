using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AppAssetSentinel.Core.Adapters;
using AppAssetSentinel.Core.Operations;
using AppAssetSentinel.Core.Policy;

namespace AppAssetSentinel.App;

/// <summary>
/// AUDIT W12: the MCP surface, so API / CLI / GUI / MCP all run under one authorization and
/// execution policy.
///
/// This server is deliberately read-only. Every tool it advertises is a query, and it holds
/// the same CapabilityPolicy instance as the GUI, the HTTP API and the CLI, so a capability
/// this process refuses cannot be obtained by asking over MCP. Tools that would mutate user
/// data are not advertised at all, and a call naming one is refused with the policy reason
/// rather than a generic error.
///
/// Transport: newline-delimited JSON-RPC 2.0 over stdio, which is the MCP stdio convention.
/// </summary>
public static class McpServer
{
    private const string ProtocolVersion = "2024-11-05";
    private const string ServerName = "appasset-sentinel";
    private const string ServerVersion = "1.0.0-r0";

    /// <summary>Run the stdio loop until stdin closes. Returns a process exit code.</summary>
    public static int Run(CapabilityPolicy policy)
    {
        // stdout is the protocol channel: everything diagnostic must go to stderr.
        var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
        var stdin = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));

        Console.Error.WriteLine($"[mcp] {ServerName} 已就绪（只读）。写入能力由能力门控决定。");

        string? line;
        while ((line = stdin.ReadLine()) != null)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            string? response;
            try
            {
                response = Handle(line, policy);
            }
            catch (Exception ex)
            {
                // Malformed input must not kill the session.
                response = ErrorResponse(null, -32603, $"内部错误：{ex.Message}");
            }

            if (response != null)
            {
                stdout.WriteLine(response);
            }
        }

        return 0;
    }

    private static string? Handle(string line, CapabilityPolicy policy)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(line);
        }
        catch (JsonException)
        {
            return ErrorResponse(null, -32700, "请求不是合法 JSON。");
        }

        if (root is not JsonObject request)
        {
            return ErrorResponse(null, -32600, "请求必须是 JSON-RPC 对象。");
        }

        string method = request["method"]?.GetValue<string>() ?? string.Empty;
        JsonNode? idNode = request["id"];
        bool isNotification = idNode == null;
        JsonNode id = idNode ?? JsonValue.Create(0)!;

        switch (method)
        {
            case "initialize":
                return Result(id, new JsonObject
                {
                    ["protocolVersion"] = ProtocolVersion,
                    ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
                    ["serverInfo"] = new JsonObject
                    {
                        ["name"] = ServerName,
                        ["version"] = ServerVersion
                    }
                });

            case "notifications/initialized":
            case "initialized":
                return null; // notification: no response

            case "ping":
                return Result(id, new JsonObject());

            case "tools/list":
                return Result(id, new JsonObject { ["tools"] = DescribeTools() });

            case "tools/call":
                return CallTool(id, request["params"] as JsonObject, policy);

            default:
                return isNotification ? null : ErrorResponse(id, -32601, $"不支持的方法：{method}");
        }
    }

    /// <summary>
    /// The advertised surface. Only queries appear here: a mutating capability is not offered
    /// at all, so a caller cannot discover it as an option in the first place.
    /// </summary>
    private static JsonArray DescribeTools()
    {
        var tools = new JsonArray();

        tools.Add(Tool("sentinel_policy",
            "显示当前能力姿态：哪些写入已开放、哪些被关闭以及原因。只读。",
            new JsonObject { ["type"] = "object", ["properties"] = new JsonObject(), ["additionalProperties"] = false }));

        tools.Add(Tool("sentinel_adapters",
            "显示各领域的适配器状态、载荷形态与支持的写入能力，并做一次只读探测。",
            new JsonObject { ["type"] = "object", ["properties"] = new JsonObject(), ["additionalProperties"] = false }));

        tools.Add(Tool("sentinel_operations",
            "列出迁移操作记录，用于判断上一次操作的真实状态。只读。",
            new JsonObject { ["type"] = "object", ["properties"] = new JsonObject(), ["additionalProperties"] = false }));

        tools.Add(Tool("sentinel_recovery",
            "针对某个任务，对照磁盘实际布局判定真实状态（磁盘优先于日志）。只读。",
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["task_id"] = new JsonObject { ["type"] = "string", ["description"] = "操作记录中的 task_id" }
                },
                ["required"] = new JsonArray("task_id"),
                ["additionalProperties"] = false
            }));

        tools.Add(Tool("sentinel_evaluate_migration",
            "判断某个路径是否可作为迁移候选（载荷形态与领域能力），不做任何修改。",
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["domain"] = new JsonObject { ["type"] = "string", ["description"] = "如 ai_models / docker_disk" },
                    ["path"] = new JsonObject { ["type"] = "string", ["description"] = "待评估的路径" }
                },
                ["required"] = new JsonArray("domain", "path"),
                ["additionalProperties"] = false
            }));

        return tools;
    }

    private static JsonObject Tool(string name, string description, JsonObject schema) => new()
    {
        ["name"] = name,
        ["description"] = description,
        ["inputSchema"] = schema
    };

    private static string CallTool(JsonNode id, JsonObject? requestParams, CapabilityPolicy policy)
    {
        string name = requestParams?["name"]?.GetValue<string>() ?? string.Empty;
        var args = requestParams?["arguments"] as JsonObject ?? new JsonObject();

        try
        {
            string text = name switch
            {
                "sentinel_policy" => RenderPolicy(policy),
                "sentinel_adapters" => RenderAdapters(policy),
                "sentinel_operations" => RenderOperations(),
                "sentinel_recovery" => RenderRecovery(args),
                "sentinel_evaluate_migration" => RenderEligibility(args),

                // A mutating name is refused with the policy reason, not a generic error, and
                // it is never advertised in tools/list.
                _ => throw new McpToolException(
                    $"未提供工具「{name}」。本接口为只读，写入能力必须通过受门控的接口执行。")
            };

            return Result(id, new JsonObject
            {
                ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
                ["isError"] = false
            });
        }
        catch (McpToolException ex)
        {
            return Result(id, new JsonObject
            {
                ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = ex.Message }),
                ["isError"] = true
            });
        }
    }

    private sealed class McpToolException : Exception
    {
        public McpToolException(string message) : base(message) { }
    }

    private static string RenderPolicy(CapabilityPolicy policy)
    {
        var lines = new List<string>
        {
            $"策略档位：{(policy.AllowsMutation(Capability.VaultRelocate) ? "R1-relocation-verified" : "R0-safe-observation")}",
            string.Empty
        };

        foreach (var posture in policy.Describe())
        {
            lines.Add($"{posture.Capability,-22}{posture.State,-14}{(posture.AllowsMutation ? "允许写入" : "只读"),-10}{posture.Reason}");
        }

        return string.Join('\n', lines);
    }

    private static string RenderAdapters(CapabilityPolicy policy)
    {
        var lines = new List<string>
        {
            $"写入姿态：{(policy.AllowsMutation(Capability.VaultRelocate) ? "R1（仅已验收领域可写）" : "R0（全部只读）")}",
            string.Empty
        };

        foreach (var descriptor in AdapterRegistry.Describe())
        {
            lines.Add($"{descriptor.Domain,-20}{descriptor.PayloadKind,-14}{descriptor.WriteCapability,-16}{descriptor.ReadOnlyReason}");
        }

        return string.Join('\n', lines);
    }

    private static string RenderOperations()
    {
        var log = new OperationLog(OperationLog.DefaultDirectory);
        var records = log.LoadAll();

        if (records.Count == 0)
        {
            return "没有操作记录。";
        }

        var lines = new List<string> { $"记录数：{records.Count}", string.Empty };
        foreach (var record in records)
        {
            lines.Add($"[{record.State}] {record.AssetName}  task={record.TaskId}");
            lines.Add($"  源  : {record.SourcePath}");
            lines.Add($"  目标: {record.TargetPath}");
            lines.Add($"  备份: {record.SourceBackupPath}（{record.BackupDisposition}）");
        }

        return string.Join('\n', lines);
    }

    private static string RenderRecovery(JsonObject args)
    {
        string taskId = args["task_id"]?.GetValue<string>() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(taskId))
        {
            throw new McpToolException("缺少 task_id。");
        }

        var log = new OperationLog(OperationLog.DefaultDirectory);
        var assessment = RecoveryInspector.Inspect(log, taskId);

        var json = JsonSerializer.Serialize(assessment, new JsonSerializerOptions { WriteIndented = true });
        return json;
    }

    private static string RenderEligibility(JsonObject args)
    {
        string domain = args["domain"]?.GetValue<string>() ?? string.Empty;
        string path = args["path"]?.GetValue<string>() ?? string.Empty;

        if (string.IsNullOrWhiteSpace(domain) || string.IsNullOrWhiteSpace(path))
        {
            throw new McpToolException("需要同时提供 domain 与 path。");
        }

        var (eligible, reason) = AdapterRegistry.EvaluateMigrationEligibility(domain, path);

        return $"领域: {domain}\n路径: {path}\n是否具备迁移资格: {(eligible ? "是" : "否")}\n说明: {reason}";
    }

    private static string Result(JsonNode id, JsonNode result) => new JsonObject
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id.DeepClone(),
        ["result"] = result
    }.ToJsonString();

    private static string ErrorResponse(JsonNode? id, int code, string message) => new JsonObject
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id?.DeepClone() ?? null,
        ["error"] = new JsonObject { ["code"] = code, ["message"] = message }
    }.ToJsonString();
}