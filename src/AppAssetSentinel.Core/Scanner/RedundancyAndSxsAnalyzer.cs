using System.Text.RegularExpressions;
using AppAssetSentinel.Core.Models;

namespace AppAssetSentinel.Core.Scanner;

public static class RedundancyAndSxsAnalyzer
{
    private static readonly string[] SxsKeywords =
    {
        "visual studio", "visualstudio", "生成工具", "build tools",
        ".net", "dotnet", "visual c++", "vcredist", "cuda", "directx",
        "sdk", "software development kit", "msvc", "crt", "msbuild"
    };

    public static void Analyze(List<SoftwareAsset> apps)
    {
        var coreGroups = new Dictionary<string, List<SoftwareAsset>>();

        foreach (var a in apps)
        {
            string nameLower = a.DisplayName.ToLowerInvariant();

            // 1. Is this a Side-by-Side (SxS) developer toolchain, SDK, runtime or driver?
            bool isSxs = SxsKeywords.Any(k => nameLower.Contains(k)) ||
                         a.Category == "system_runtime" ||
                         a.IsProtected ||
                         a.RiskLevel is "critical_core" or "important_runtime" ||
                         a.AssetType is "sdk_toolchain" or "runtime_environment" or "hardware_driver" or "system_service";

            if (isSxs)
            {
                // NEVER treat as redundant!
                a.RedundancyInfo = null;

                // Attach SxS educational guidance banner
                if (SxsKeywords.Any(k => nameLower.Contains(k)))
                {
                    a.SxsInfo = new SxsInfo
                    {
                        Tip = "【💡 官方并行共存 (Side-by-Side) 基础设施说明】：Windows 下此类底层编译构建工具链与系统运行库按设计必须支持多版本隔离共存。不同年代的工程与软件强依赖特定版本的工具集底座（例如特定旧项目强依赖 Visual Studio 2019 / v142 工具集，现代项目使用 VS 2022 / v143；老软件依赖旧版 VC++ / .NET）。它们各司其职、互不干扰，属于系统核心构建基础设施，绝非冗余垃圾，严禁随意删除！"
                    };
                }
                continue;
            }

            // 2. For regular desktop applications, diagnose real duplicate redundancies
            string clean = Regex.Replace(nameLower, @"[^a-zA-Z0-9\u4e00-\u9fa5]", "");
            string root = Regex.Replace(clean, @"(version|\d+|x64|amd64|64bit)", "");
            if (root.Length >= 4)
            {
                if (!coreGroups.ContainsKey(root)) coreGroups[root] = new List<SoftwareAsset>();
                coreGroups[root].Add(a);
            }
        }

        // Attach redundancy only to genuine GUI software duplicates
        foreach (var (_, group) in coreGroups)
        {
            if (group.Count > 1)
            {
                // Sort: active process first, then by size
                group.Sort((x, y) =>
                {
                    int runX = x.IsRunning ? 1 : 0;
                    int runY = y.IsRunning ? 1 : 0;
                    if (runX != runY) return runY.CompareTo(runX);
                    return y.EstimatedSizeBytes.CompareTo(x.EstimatedSizeBytes);
                });

                var primary = group[0];
                double primaryMb = Math.Round((double)primary.EstimatedSizeBytes / (1024 * 1024), 1);
                string primarySz = primaryMb > 1024 ? $"{Math.Round(primaryMb / 1024, 2)} GB" : $"{primaryMb} MB";

                for (int idx = 0; idx < group.Count; idx++)
                {
                    var item = group[idx];
                    double secMb = Math.Round((double)item.EstimatedSizeBytes / (1024 * 1024), 1);
                    string secSz = secMb > 1024 ? $"{Math.Round(secMb / 1024, 2)} GB" : $"{secMb} MB";

                    item.RedundancyInfo = new RedundancyInfo
                    {
                        HasRedundancy = true,
                        IsPrimary = (idx == 0),
                        Role = idx == 0 ? "primary" : "secondary",
                        SiblingCount = group.Count - 1,
                        Tip = idx == 0
                            ? $"【系统主运行版本】当前系统活跃调度此版本。系统中另检测到 {group.Count - 1} 处历史/备用副本。"
                            : $"⚠️【检测到功能重复冗余】：系统中已在「{primary.InstallLocation}」安装并运行主版本 ({primary.DisplayName}，占用 {primarySz})。本副本位于「{item.InstallLocation}」(仅占用 {secSz}) 属于重复副本。如无需单独使用此独立副本，可考虑评估卸载清理。"
                    };
                }
            }
        }
    }
}
