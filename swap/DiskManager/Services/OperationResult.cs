namespace DiskManager.Services;

/// <summary>一次写操作的结果。界面只认这个类型，避免到处 try/catch。</summary>
internal sealed class OperationResult
{
    private OperationResult(bool success, string message)
    {
        Success = success;
        Message = message;
    }

    public bool Success { get; }

    /// <summary>失败原因或成功后的补充说明，可直接显示给用户。</summary>
    public string Message { get; }

    public static OperationResult Ok(string message = "") => new(true, message);

    public static OperationResult Fail(string message) => new(false, message);

    /// <summary>把多个步骤的结果合并；任一步失败即整体失败。</summary>
    public static OperationResult Combine(params OperationResult[] results)
    {
        OperationResult? failed = results.FirstOrDefault(r => !r.Success);
        if (failed is not null)
        {
            return failed;
        }

        string message = string.Join(" ", results.Where(r => r.Message.Length > 0).Select(r => r.Message));
        return Ok(message);
    }
}
