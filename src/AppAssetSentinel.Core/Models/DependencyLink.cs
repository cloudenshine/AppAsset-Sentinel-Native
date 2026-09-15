using System.Text.Json.Serialization;

namespace AppAssetSentinel.Core.Models;

public class DependencyLink
{
    [JsonPropertyName("upstream_software_id")]
    public string UpstreamSoftwareId { get; set; } = string.Empty;

    [JsonPropertyName("downstream_software_id")]
    public string DownstreamSoftwareId { get; set; } = string.Empty;

    [JsonPropertyName("upstream_name")]
    public string UpstreamName { get; set; } = string.Empty;

    [JsonPropertyName("downstream_name")]
    public string DownstreamName { get; set; } = string.Empty;

    [JsonPropertyName("dependency_type")]
    public string DependencyType { get; set; } = "runtime_environment"; // runtime_environment, tool_dependency, hardware_acceleration, platform_subsystem

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;
}
