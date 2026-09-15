using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using AppAssetSentinel.Core.Models;
using AppAssetSentinel.Core.Semantic;

namespace AppAssetSentinel.Core.Shield;

public class DependencyRule
{
    [JsonPropertyName("upstream_pattern")]
    public string UpstreamPattern { get; set; } = string.Empty;

    [JsonPropertyName("downstream_pattern")]
    public string DownstreamPattern { get; set; } = string.Empty;

    [JsonPropertyName("dependency_type")]
    public string DependencyType { get; set; } = "runtime_environment";

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    [JsonIgnore]
    public Regex? CompiledUpstream { get; set; }

    [JsonIgnore]
    public Regex? CompiledDownstream { get; set; }
}

public class DependencyShield
{
    private readonly List<DependencyRule> _rules = new();
    private readonly List<DependencyLink> _graph = new();

    public DependencyShield(string? rulesJsonPath = null)
    {
        LoadRules(rulesJsonPath);
    }

    public void LoadRules(string? customPath = null)
    {
        _rules.Clear();
        string jsonContent = string.Empty;

        if (!string.IsNullOrEmpty(customPath) && File.Exists(customPath))
        {
            try { jsonContent = File.ReadAllText(customPath); } catch { }
        }
        else
        {
            var rulesDir = SemanticRuleEngine.FindRulesDirectory();
            if (rulesDir != null)
            {
                var targetFile = Path.Combine(rulesDir, "dependency_rules.json");
                if (File.Exists(targetFile))
                {
                    try { jsonContent = File.ReadAllText(targetFile); } catch { }
                }
            }
        }

        if (!string.IsNullOrEmpty(jsonContent))
        {
            try
            {
                var options = new JsonSerializerOptions 
                { 
                    PropertyNameCaseInsensitive = true,
                    PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
                };
                var parsed = JsonSerializer.Deserialize<List<DependencyRule>>(jsonContent, options);
                if (parsed != null)
                {
                    foreach (var r in parsed)
                    {
                        try
                        {
                            if (!string.IsNullOrEmpty(r.UpstreamPattern))
                                r.CompiledUpstream = new Regex(r.UpstreamPattern, RegexOptions.IgnoreCase | RegexOptions.Compiled);
                            if (!string.IsNullOrEmpty(r.DownstreamPattern))
                                r.CompiledDownstream = new Regex(r.DownstreamPattern, RegexOptions.IgnoreCase | RegexOptions.Compiled);
                            _rules.Add(r);
                        }
                        catch { }
                    }
                }
            }
            catch { }
        }
    }

    public List<DependencyLink> BuildGraph(List<SoftwareAsset> apps)
    {
        _graph.Clear();
        var links = new List<DependencyLink>();
        var seenLinks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Track key upstreams
        var pythonApp = apps.FirstOrDefault(a => a.DisplayName.Contains("Python", StringComparison.OrdinalIgnoreCase) && !a.DisplayName.Contains("Launcher"));
        var cudaApp = apps.FirstOrDefault(a => a.DisplayName.Contains("CUDA", StringComparison.OrdinalIgnoreCase));
        var gitApp = apps.FirstOrDefault(a => a.DisplayName.StartsWith("Git", StringComparison.OrdinalIgnoreCase));
        var wslApp = apps.FirstOrDefault(a => a.DisplayName.Contains("WSL", StringComparison.OrdinalIgnoreCase) || a.DisplayName.Contains("Subsystem for Linux", StringComparison.OrdinalIgnoreCase));
        var vcredistApp = apps.FirstOrDefault(a => a.DisplayName.Contains("Visual C++", StringComparison.OrdinalIgnoreCase));
        var ollamaApp = apps.FirstOrDefault(a => a.DisplayName.Contains("Ollama", StringComparison.OrdinalIgnoreCase));

        // 1. Match from Curated Rules Knowledge Base
        foreach (var upstream in apps)
        {
            string upTarget = $"{upstream.DisplayName} {upstream.MainExecutable}".ToLowerInvariant();

            foreach (var rule in _rules)
            {
                if (rule.CompiledUpstream == null || !rule.CompiledUpstream.IsMatch(upTarget))
                    continue;

                foreach (var downstream in apps)
                {
                    if (downstream.Id == upstream.Id) continue;
                    string downTarget = $"{downstream.DisplayName} {downstream.MainExecutable}".ToLowerInvariant();

                    if (rule.CompiledDownstream != null && rule.CompiledDownstream.IsMatch(downTarget))
                    {
                        string sig = $"{upstream.Id}->{downstream.Id}";
                        if (seenLinks.Add(sig))
                        {
                            links.Add(new DependencyLink
                            {
                                UpstreamSoftwareId = upstream.Id,
                                DownstreamSoftwareId = downstream.Id,
                                UpstreamName = upstream.DisplayName,
                                DownstreamName = downstream.DisplayName,
                                DependencyType = rule.DependencyType,
                                Description = rule.Description
                            });
                        }
                    }
                }
            }
        }

        // 2. Zero-Rule Dynamic Binary/Library Static Feature Peeker
        // Automatically discovers dependencies without requiring human-written rules!
        foreach (var app in apps)
        {
            if (string.IsNullOrEmpty(app.InstallLocation) || !Directory.Exists(app.InstallLocation))
                continue;

            try
            {
                bool hasCudaDll = false;
                bool hasPythonDll = false;
                bool hasVcDll = false;
                bool hasGitRef = false;

                var dirInfo = new DirectoryInfo(app.InstallLocation);
                foreach (var f in dirInfo.EnumerateFiles("*.*", new EnumerationOptions { MaxRecursionDepth = 2, RecurseSubdirectories = true }).Take(60))
                {
                    string fName = f.Name.ToLowerInvariant();
                    if (fName.Contains("cuda") || fName.Contains("cudart") || fName.Contains("cublas")) hasCudaDll = true;
                    if (fName.Contains("python") || f.Extension.Equals(".py", StringComparison.OrdinalIgnoreCase)) hasPythonDll = true;
                    if (fName.Contains("msvcp") || fName.Contains("vcruntime")) hasVcDll = true;
                    if (fName.Equals("git.exe") || fName.Equals(".git")) hasGitRef = true;
                }

                // Auto-link to CUDA
                if (hasCudaDll && cudaApp != null && cudaApp.Id != app.Id)
                {
                    string sig = $"{cudaApp.Id}->{app.Id}";
                    if (seenLinks.Add(sig))
                    {
                        links.Add(new DependencyLink
                        {
                            UpstreamSoftwareId = cudaApp.Id,
                            DownstreamSoftwareId = app.Id,
                            UpstreamName = cudaApp.DisplayName,
                            DownstreamName = app.DisplayName,
                            DependencyType = "hardware_acceleration",
                            Description = "【二进制动态嗅探】检测到软件内部调用了 NVIDIA CUDA 动态加速运行库。"
                        });
                    }
                }

                // Auto-link to Python
                if (hasPythonDll && pythonApp != null && pythonApp.Id != app.Id)
                {
                    string sig = $"{pythonApp.Id}->{app.Id}";
                    if (seenLinks.Add(sig))
                    {
                        links.Add(new DependencyLink
                        {
                            UpstreamSoftwareId = pythonApp.Id,
                            DownstreamSoftwareId = app.Id,
                            UpstreamName = pythonApp.DisplayName,
                            DownstreamName = app.DisplayName,
                            DependencyType = "runtime_environment",
                            Description = "【二进制动态嗅探】检测到软件内部集成了 Python 解释器或执行脚本。"
                        });
                    }
                }

                // Auto-link to Git
                if (hasGitRef && gitApp != null && gitApp.Id != app.Id)
                {
                    string sig = $"{gitApp.Id}->{app.Id}";
                    if (seenLinks.Add(sig))
                    {
                        links.Add(new DependencyLink
                        {
                            UpstreamSoftwareId = gitApp.Id,
                            DownstreamSoftwareId = app.Id,
                            UpstreamName = gitApp.DisplayName,
                            DownstreamName = app.DisplayName,
                            DependencyType = "tool_dependency",
                            Description = "【二进制动态嗅探】检测到软件依赖 Git 命令进行版本克隆或插件拉取。"
                        });
                    }
                }

                // Auto-link to Visual C++
                if (hasVcDll && vcredistApp != null && vcredistApp.Id != app.Id)
                {
                    string sig = $"{vcredistApp.Id}->{app.Id}";
                    if (seenLinks.Add(sig))
                    {
                        links.Add(new DependencyLink
                        {
                            UpstreamSoftwareId = vcredistApp.Id,
                            DownstreamSoftwareId = app.Id,
                            UpstreamName = vcredistApp.DisplayName,
                            DownstreamName = app.DisplayName,
                            DependencyType = "runtime_environment",
                            Description = "【二进制动态嗅探】检测到软件依赖 Visual C++ 动态链接库运行时。"
                        });
                    }
                }
            }
            catch { }
        }

        // AUDIT A16: the evidence graph keeps every observed edge. Cycles are a real
        // property of some installs, and deleting an edge to make a pretty DAG destroys
        // the very fact we collected. Traversal safety is handled by CountCyclicEdges
        // and by callers that walk with visited-sets.
        _graph.AddRange(links);
        return links;
    }

    /// <summary>
    /// Counts edges that participate in a cycle (same-node or mutually reachable).
    /// Reported for display only — the edges themselves are preserved.
    /// </summary>
    public static int CountCyclicEdges(List<DependencyLink> links)
    {
        var adj = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var link in links)
        {
            if (!adj.TryGetValue(link.UpstreamSoftwareId, out var set))
            {
                set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                adj[link.UpstreamSoftwareId] = set;
            }
            set.Add(link.DownstreamSoftwareId);
        }

        bool Reachable(string from, string to)
        {
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var stack = new Stack<string>();
            stack.Push(from);

            while (stack.Count > 0)
            {
                string node = stack.Pop();
                if (string.Equals(node, to, StringComparison.OrdinalIgnoreCase)) return true;
                if (!visited.Add(node)) continue;
                if (!adj.TryGetValue(node, out var next)) continue;
                foreach (var n in next) stack.Push(n);
            }

            return false;
        }

        int cyclic = 0;
        foreach (var link in links)
        {
            if (string.Equals(link.UpstreamSoftwareId, link.DownstreamSoftwareId, StringComparison.OrdinalIgnoreCase) ||
                Reachable(link.DownstreamSoftwareId, link.UpstreamSoftwareId))
            {
                cyclic++;
            }
        }

        return cyclic;
    }

    public SafetyReport EvaluateUninstallSafety(string softwareId, List<SoftwareAsset> allApps)
    {
        var target = allApps.FirstOrDefault(a => a.Id == softwareId);
        var report = new SafetyReport();

        if (target == null)
        {
            report.CanUninstall = false;
            report.AllowOverride = false;
            report.BlockReason = $"Software ID '{softwareId}' not found in installed assets.";
            return report;
        }

        string nameLower = target.DisplayName.ToLowerInvariant();

        // 1. True Windows Kernel / System Component (Strictly immutable)
        if (target.RegistryKeyPath.Contains("SystemComponent", StringComparison.OrdinalIgnoreCase) ||
            (target.Publisher.Contains("Microsoft Windows", StringComparison.OrdinalIgnoreCase) && target.Category == "system_hardware"))
        {
            report.CanUninstall = false;
            report.AllowOverride = false;
            report.IsCriticalSystem = true;
            report.BlockReason = $"【系统内置底层防护】'{target.DisplayName}' 属于 Windows 核心内置组件，绝对禁止卸载。";
            return report;
        }

        // 2. Developer Runtimes / Tools / AI Workstations (Cascade Protected, CAN BE UNLOCKED)
        var downstreams = _graph.Where(l => l.UpstreamSoftwareId == softwareId).ToList();

        if (target.IsProtected || target.HeatLevel == "infrastructure" || downstreams.Count > 0)
        {
            report.CanUninstall = false;
            report.IsProtectedInfra = true;
            report.AllowOverride = true; // User can unlock with informed consent

            if (downstreams.Count > 0)
            {
                report.DependentApps = downstreams.Select(d => $"{d.DownstreamName} ({d.Description})").ToList();
            }

            // Synthesize cascade impacts and prerequisite advice
            if (nameLower.Contains("docker"))
            {
                report.BlockReason = "【开发环境底座保护】Docker Desktop 承载着本地容器与数据卷生命周期。";
                report.CascadeImpacts = new List<string>
                {
                    "WSL2 / Hyper-V 内部的所有运行中容器、数据库实例将随之完全停机",
                    "本地持久化数据卷（ext4.vhdx）与网络端口映射服务将失效",
                    "基于 Docker 运行的本地 Web 服务、微服务或 AI 容器将无法访问"
                };
                report.PrerequisiteAdvice = "建议操作顺序：若确认彻底停用，请先执行「docker ps」确认无重要容器正在运行，必要时导出容器数据镜像，再解锁执行卸载。";
            }
            else if (nameLower.Contains("node"))
            {
                report.BlockReason = "【开发环境底座保护】Node.js 是前端构建、npm 生态与命令行 AI Agent 的执行底座。";
                report.CascadeImpacts = new List<string>
                {
                    "全局安装的 npm/npx 模块工具链（pnpm, yarn, tsx 等）将完全失效",
                    "本地所有前端工程项目（Vue, React, Next.js）的构建与开发服务器将无法启动",
                    "依赖 Node.js 调度的本地 CLI 自动化脚本将直接报错退出"
                };
                report.PrerequisiteAdvice = "建议操作顺序：如仅为切换多版本，推荐优先安装使用 nvm-windows 进行多版本并存管理，无需完全清理底层运行时。";
            }
            else if (nameLower.Contains("git"))
            {
                report.BlockReason = "【代码协同工具底座保护】Git 是版本控制与第三方插件自动拉取的核心支撑。";
                report.CascadeImpacts = new List<string>
                {
                    "VS Code、Cursor 等 IDE 的源码分支管理、提交与 Diff 历史比对将失效",
                    "ComfyUI 自定义节点管理器 (ComfyUI-Manager) 将无法拉取或更新扩展插件",
                    "所有基于命令行与自动化脚本的「git clone / git pull」将报错退出"
                };
                report.PrerequisiteAdvice = "建议操作顺序：卸载前请确认本地全部开发代码仓库的未提交修改已安全推送到远端（GitHub / Gitee）。";
            }
            else if (nameLower.Contains("python"))
            {
                report.BlockReason = "【AI 与通用编程底座保护】Python 是本地全系 AI 框架与自动化工具链的执行环境。";
                report.CascadeImpacts = new List<string>
                {
                    "依赖系统 Python 的 ComfyUI、Stable Diffusion WebUI 将彻底失去运行解释器",
                    "本地大量机器学习脚本与 pip 安装的加速库将全部报错「python not found」",
                    "基于系统 Python 的各类自动化运维脚本将无法执行"
                };
                report.PrerequisiteAdvice = "建议操作顺序：若需废弃，建议先卸载清理依赖此 Python 的下游 AI 工作台与环境，再执行本底座清理。";
            }
            else if (nameLower.Contains("ollama"))
            {
                report.BlockReason = "【本地大模型推理底座保护】Ollama 是本地开源 AI 模型的计算与 API 调度核心。";
                report.CascadeImpacts = new List<string>
                {
                    "Cherry Studio、Page Assist、Open WebUI 等所有客户端将无法连接 11434 端口",
                    "本地已下载的所有大模型（Qwen, DeepSeek, Llama 等）推理服务将立即中断"
                };
                report.PrerequisiteAdvice = "建议操作顺序：若仅为清理 C 盘空间，强烈建议使用【一键无损迁移】将数十 GB 模型软链接至 D 盘，切勿直接卸载服务！";
            }
            else if (nameLower.Contains("visual studio") || nameLower.Contains("生成工具"))
            {
                report.BlockReason = "【底层 C/C++ 构建链保护】Visual Studio 生成工具是原生代码与 Python 扩展编译核心。";
                report.CascadeImpacts = new List<string>
                {
                    "本地使用 pip 或 uv 编译包含 C++ 扩展或 CUDA 算子的第三方库将直接报错",
                    "本地 Node-gyp 原生模块构建将由于缺失 MSVC 编译器而失败"
                };
                report.PrerequisiteAdvice = "建议操作顺序：此类构建工具支持多版本并行共存（SxS），如无特殊原因严禁卸载。";
            }
            else
            {
                report.BlockReason = $"【运行环境底座保护】'{target.DisplayName}' 属于系统运行环境或底层依赖。";
                if (downstreams.Count > 0)
                {
                    report.CascadeImpacts = downstreams.Select(d => $"下游依赖软件「{d.DownstreamName}」：{d.Description}").ToList();
                }
                report.PrerequisiteAdvice = "建议操作顺序：若确认不再需要，请先评估下游受影响模块，再解锁强行卸载。";
            }

            return report;
        }

        report.CanUninstall = true;
        report.AllowOverride = true;
        return report;
    }

    /// <summary>
    /// DFS cycle breaker to prevent deadlock in circular dependency declarations.
    /// </summary>
    public static List<DependencyLink> BreakCycles(List<DependencyLink> links)
    {
        var result = new List<DependencyLink>();
        var adj = new Dictionary<string, HashSet<string>>();

        bool HasPath(string from, string to, HashSet<string> visited)
        {
            if (from == to) return true;
            if (!visited.Add(from)) return false;
            if (!adj.ContainsKey(from)) return false;

            foreach (var next in adj[from])
            {
                if (HasPath(next, to, visited)) return true;
            }
            return false;
        }

        foreach (var link in links)
        {
            if (HasPath(link.DownstreamSoftwareId, link.UpstreamSoftwareId, new HashSet<string>()))
            {
                continue;
            }

            if (!adj.ContainsKey(link.UpstreamSoftwareId))
            {
                adj[link.UpstreamSoftwareId] = new HashSet<string>();
            }
            adj[link.UpstreamSoftwareId].Add(link.DownstreamSoftwareId);
            result.Add(link);
        }

        return result;
    }
}
