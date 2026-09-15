using AppAssetSentinel.Core.Scanner;

namespace AppAssetSentinel.Core.Migration;

public static class DriftWatchdog
{
    public static List<VaultRegistration> InspectAndAuditDrifts()
    {
        var list = AssetVaultEngine.GetActiveRegistrations();

        foreach (var reg in list)
        {
            reg.LastVerifiedAt = DateTime.UtcNow;

            if (!Directory.Exists(reg.VirtualAnchorPath))
            {
                reg.IsJunctionIntact = false;
                reg.DriftDetected = true;
                reg.DriftDetails = "原路径联接点已被移除或脱机。";
                continue;
            }

            var junc = FastDirectorySizer.GetJunctionInfo(reg.VirtualAnchorPath);
            if (!junc.IsJunction)
            {
                // DRIFT: Software update created a regular physical folder over the junction!
                reg.IsJunctionIntact = false;
                reg.DriftDetected = true;
                reg.DriftDetails = "检测到软件升级或重装覆盖了软链接，导致数据漂移回系统盘！";
            }
            else
            {
                // Verify target path matches
                string normTarget = Path.GetFullPath(junc.TargetPath).TrimEnd('\\');
                string normExpected = Path.GetFullPath(reg.PhysicalVaultPath).TrimEnd('\\');

                if (!string.Equals(normTarget, normExpected, StringComparison.OrdinalIgnoreCase))
                {
                    reg.IsJunctionIntact = false;
                    reg.DriftDetected = true;
                    reg.DriftDetails = $"软链接目标发生偏离: 当前指向 {normTarget}, 预期指向 {normExpected}";
                }
                else
                {
                    reg.IsJunctionIntact = true;
                    reg.DriftDetected = false;
                    reg.DriftDetails = "双向联接状态优良，内核级转发无损穿透。";
                }
            }
        }

        AssetVaultEngine.SaveRegistrations(list);
        return list;
    }

    public static (bool Success, string Message) AutoHealDrift(string registrationId)
    {
        var list = AssetVaultEngine.GetActiveRegistrations();
        var target = list.FirstOrDefault(r => r.Id == registrationId);

        if (target == null)
        {
            return (false, "未找到该资产注册项。");
        }

        string anchor = target.VirtualAnchorPath;
        string vault = target.PhysicalVaultPath;

        if (!Directory.Exists(vault))
        {
            return (false, $"物理仓库路径不存在: {vault}");
        }

        try
        {
            // 1. If anchor is a plain physical folder with newly drifted files, merge them into vault
            if (Directory.Exists(anchor) && !FastDirectorySizer.IsReparsePoint(anchor))
            {
                var dirInfo = new DirectoryInfo(anchor);
                foreach (var file in dirInfo.EnumerateFiles("*.*", SearchOption.AllDirectories))
                {
                    string relPath = Path.GetRelativePath(anchor, file.FullName);
                    string destPath = Path.Combine(vault, relPath);
                    string destDir = Path.GetDirectoryName(destPath) ?? vault;
                    if (!Directory.Exists(destDir)) Directory.CreateDirectory(destDir);

                    // Copy newer file
                    if (!File.Exists(destPath) || file.LastWriteTime > new FileInfo(destPath).LastWriteTime)
                    {
                        File.Copy(file.FullName, destPath, true);
                    }
                }

                // Delete drifted plain directory
                Directory.Delete(anchor, true);
            }
            else if (Directory.Exists(anchor) && FastDirectorySizer.IsReparsePoint(anchor))
            {
                JunctionEngine.RemoveJunction(anchor, out _);
            }

            // 2. Re-create pristine NTFS Junction
            if (!JunctionEngine.CreateJunction(anchor, vault, out var err))
            {
                return (false, $"自愈重建软链接失败: {err}");
            }

            target.IsJunctionIntact = true;
            target.DriftDetected = false;
            target.DriftDetails = "已成功完成自愈！漂移数据已合并入物理仓库，目录联接已重新加固。";
            target.LastVerifiedAt = DateTime.UtcNow;

            AssetVaultEngine.SaveRegistrations(list);
            return (true, "自愈成功！漂移数据已无损合并，软链接已重新锁死。");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }
}
