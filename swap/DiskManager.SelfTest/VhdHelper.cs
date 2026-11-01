using System.Diagnostics;
using System.IO;
using System.Text;

namespace DiskManager.SelfTest;

/// <summary>
/// 测试用 VHDX 的创建 / 挂载 / 卸载。
///
/// 实现走 PowerShell 的 VHD 命令（New-VHD / Mount-VHD / Dismount-VHD）：
/// 本机 Virtual Disk Service(vds) 处于停止状态，diskpart 的 vdisk 命令会静默失败，
/// 而 VHD 命令直接调用虚拟磁盘驱动，不依赖该服务。全程只操作自己创建的文件。
/// </summary>
internal static class VhdHelper
{
    /// <summary>测试盘容量（MB）。</summary>
    public const int TestDiskSizeMb = 2048;

    /// <summary>创建（若不存在）并挂载 VHDX，挂载时不分配驱动器号。</summary>
    public static bool TryCreateAndAttach(string vhdxPath, out string error, out string output)
    {
        // 使用 $$""" 原始字符串：PowerShell 的花括号是字面量，插值需要写成 {{...}}
        string script = $$"""
            $ErrorActionPreference = 'Stop'
            try {
                if (-not (Test-Path -LiteralPath '{{vhdxPath}}')) {
                    New-VHD -Path '{{vhdxPath}}' -SizeBytes {{TestDiskSizeMb}}MB -Dynamic | Out-Null
                    Write-Output 'created'
                } else {
                    Write-Output 'already-exists'
                }
                Mount-VHD -Path '{{vhdxPath}}' -NoDriveLetter
                Write-Output 'mounted'
                exit 0
            } catch {
                Write-Output ('ERROR: ' + $_.Exception.Message)
                exit 1
            }
            """;

        return RunPowerShell(script, vhdxPath, out error, out output);
    }

    /// <summary>卸载 VHDX（保留文件，不做删除）。</summary>
    public static bool TryDetach(string vhdxPath, out string error, out string output)
    {
        string script = $$"""
            $ErrorActionPreference = 'Stop'
            try {
                Dismount-VHD -Path '{{vhdxPath}}'
                Write-Output 'dismounted'
                exit 0
            } catch {
                Write-Output ('ERROR: ' + $_.Exception.Message)
                exit 1
            }
            """;

        return RunPowerShell(script, vhdxPath, out error, out output);
    }

    /// <summary>把脚本写入 .ps1 再执行，避免命令行转义问题。</summary>
    private static bool RunPowerShell(string script, string vhdxPath, out string error, out string output)
    {
        string directory = Path.GetDirectoryName(vhdxPath) ?? Path.GetTempPath();
        string scriptPath = Path.Combine(directory, $"vhd-command-{Guid.NewGuid():N}.ps1");

        // PowerShell 5.1 读取无 BOM 的 UTF-8 会按本地代码页解析，这里显式写 BOM。
        File.WriteAllText(scriptPath, script, new UTF8Encoding(true));

        try
        {
            using Process process = new()
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{scriptPath}\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                },
            };

            process.Start();
            string stdout = process.StandardOutput.ReadToEnd();
            string stderr = process.StandardError.ReadToEnd();
            process.WaitForExit(180_000);
            output = stdout.Trim();

            if (process.ExitCode == 0)
            {
                error = string.Empty;
                return true;
            }

            error = string.IsNullOrWhiteSpace(stdout) ? stderr.Trim() : stdout.Trim();
            return false;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            output = string.Empty;
            return false;
        }
        finally
        {
            // 本机约定：不删除文件，移动到 BACKUP\DEL 备查。
            try
            {
                string backupDir = Path.Combine(directory, "BACKUP", "DEL");
                Directory.CreateDirectory(backupDir);
                File.Move(scriptPath, Path.Combine(backupDir, Path.GetFileName(scriptPath)), overwrite: true);
            }
            catch
            {
                // 移动失败不影响测试结论
            }
        }
    }
}
