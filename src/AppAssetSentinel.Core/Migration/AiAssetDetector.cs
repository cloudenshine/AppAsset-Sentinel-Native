using AppAssetSentinel.Core.Models;
using AppAssetSentinel.Core.Scanner;

namespace AppAssetSentinel.Core.Migration;

public class AiAssetInfo
{
    public string SoftwareId { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public string AssetType { get; set; } = string.Empty; // GGUF, Safetensors, PyTorch, OllamaBlob, Onnx
    public long FileSizeBytes { get; set; } = 0;
}

public class AiAssetDetector
{
    private static readonly HashSet<string> ModelExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".safetensors", ".gguf", ".bin", ".pt", ".pth", ".onnx", ".ckpt"
    };

    public static List<AiAssetInfo> DetectAssetsInDirectory(string dirPath, int maxResults = 500)
    {
        var results = new List<AiAssetInfo>();
        if (string.IsNullOrWhiteSpace(dirPath) || !Directory.Exists(dirPath))
            return results;

        try
        {
            var stack = new Stack<string>();
            stack.Push(dirPath);

            while (stack.Count > 0 && results.Count < maxResults)
            {
                var current = stack.Pop();
                var dirInfo = new DirectoryInfo(current);

                // Detect Ollama Blobs folder specifically
                if (dirInfo.Name.Equals("blobs", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var file in dirInfo.EnumerateFiles())
                    {
                        // Ollama blobs are typically > 10MB sha256 hashes without extension
                        if (file.Length > 10 * 1024 * 1024)
                        {
                            results.Add(new AiAssetInfo
                            {
                                Path = file.FullName,
                                AssetType = "OllamaBlob",
                                FileSizeBytes = file.Length
                            });
                        }
                    }
                    continue;
                }

                foreach (var file in dirInfo.EnumerateFiles())
                {
                    var ext = file.Extension;
                    if (ModelExtensions.Contains(ext) && file.Length > 5 * 1024 * 1024) // > 5MB
                    {
                        string type = ext.TrimStart('.').ToUpperInvariant();
                        if (type == "SAFETENSORS") type = "Safetensors";
                        else if (type == "GGUF") type = "GGUF";

                        results.Add(new AiAssetInfo
                        {
                            Path = file.FullName,
                            AssetType = type,
                            FileSizeBytes = file.Length
                        });
                    }
                }

                foreach (var sub in dirInfo.EnumerateDirectories())
                {
                    // Never traverse reparse points
                    if ((sub.Attributes & FileAttributes.ReparsePoint) != FileAttributes.ReparsePoint)
                    {
                        stack.Push(sub.FullName);
                    }
                }
            }
        }
        catch { }

        return results;
    }
}
