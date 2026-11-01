using System.Management;

namespace VhdxCopier;

internal sealed class VssShadow
{
    private VssShadow(string shadowId, string deviceObject)
    {
        ShadowId = shadowId;
        DeviceObject = deviceObject;
    }

    public string ShadowId { get; }

    public string DeviceObject { get; }

    public static VssShadow Create(string volumeRoot)
    {
        using var shadowClass = new ManagementClass("Win32_ShadowCopy");
        using var inParameters = shadowClass.GetMethodParameters("Create");
        inParameters["Volume"] = volumeRoot;
        inParameters["Context"] = "ClientAccessible";

        using var outParameters = shadowClass.InvokeMethod("Create", inParameters, null);
        var returnValue = Convert.ToUInt32(outParameters["ReturnValue"]);
        if (returnValue != 0)
        {
            throw new VssException(returnValue);
        }

        var shadowId = Convert.ToString(outParameters["ShadowID"]);
        if (string.IsNullOrWhiteSpace(shadowId))
        {
            throw new InvalidOperationException("VSS 未返回卷影副本 ID。");
        }

        try
        {
            using var searcher = new ManagementObjectSearcher(
                $"SELECT DeviceObject FROM Win32_ShadowCopy WHERE ID = '{shadowId}'");

            foreach (ManagementObject shadow in searcher.Get())
            {
                using (shadow)
                {
                    var deviceObject = Convert.ToString(shadow["DeviceObject"]);
                    if (!string.IsNullOrWhiteSpace(deviceObject))
                    {
                        return new VssShadow(shadowId, deviceObject);
                    }
                }
            }

            throw new InvalidOperationException("找不到刚创建的卷影副本。");
        }
        catch
        {
            try
            {
                Delete(shadowId);
            }
            catch
            {
            }

            throw;
        }
    }

    public string BuildSourcePath(string originalPath)
    {
        var root = Path.GetPathRoot(originalPath);
        if (string.IsNullOrEmpty(root))
        {
            throw new ArgumentException("源路径无效。", nameof(originalPath));
        }

        var relativePath = originalPath
            .Substring(root.Length)
            .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        return $"{DeviceObject.TrimEnd('\\')}\\{relativePath}";
    }

    public static void Delete(string shadowId)
    {
        using var searcher = new ManagementObjectSearcher(
            $"SELECT * FROM Win32_ShadowCopy WHERE ID = '{shadowId}'");

        foreach (ManagementObject shadow in searcher.Get())
        {
            using (shadow)
            {
                shadow.Delete();
            }
        }
    }
}

internal sealed class VssException : Exception
{
    public VssException(uint code)
        : base(GetMessage(code))
    {
        Code = code;
    }

    public uint Code { get; }

    private static string GetMessage(uint code)
    {
        return code switch
        {
            0x80042301 => "VSS 遇到意外错误。",
            0x80042302 => "VSS 服务或卷影提供程序不可用。",
            0x80042306 => "卷影存储空间不足。",
            0x8004230F => "卷影提供程序返回错误，可能是卷类型不支持或存储驱动异常。",
            0x80042316 => "另一个卷影创建操作正在进行，请稍后重试。",
            0x8004231F => "卷影存储空间不足。",
            _ => $"VSS 创建失败，错误码 0x{code:X8}。"
        };
    }
}
