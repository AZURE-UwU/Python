using System.IO;
using System.Management;
using System.Text;
using DiskManager.Interop;
using DiskManager.Models;

namespace DiskManager.Services;

/// <summary>
/// 解决"挡路条件"的交互回调。服务层不依赖界面，由调用方（界面）注入。
/// </summary>
internal sealed class BlockResolutionOptions
{
    /// <summary>
    /// 界面**已经**问过用户、并得到"先分离挡路的虚拟磁盘"的确认。
    ///
    /// 注意：这里刻意做成"预先决定"，而不是在操作过程中回调界面——
    /// 操作跑在后台线程上，回调界面需要"投递消息 + 阻塞等待"，
    /// 一旦界面线程正在处理对话框，就会互相等待形成死锁（表现就是整个程序卡死）。
    /// </summary>
    public bool DetachBlockingVirtualDiskConfirmed { get; init; }

    /// <summary>界面事先准备好的宿主文件路径（自动扫描找不到时由用户指定）。</summary>
    public string? PreResolvedVirtualDiskFile { get; init; }

    /// <summary>
    /// 强制模式：跳过"先分离虚拟磁盘"，直接把该磁盘上的卷强制脱机，
    /// 然后再执行脱机/弹出——这正是 Windows 磁盘管理的做法
    /// （它不检查文件占用，强制卸载卷，正在用这些文件的程序会收到句柄失效错误）。
    ///
    /// 风险：占用文件的程序（例如还挂着的 Hyper-V 虚拟机）可能数据损坏；
    /// 因此只有在用户明确选择"强制"时才会走到这里。
    /// </summary>
    public bool ForceDismount { get; init; }

    /// <summary>
    /// 用户已经看过安全警告、并明确选择"继续"。
    ///
    /// 语义变化：安全闸门从"拦截"改成"警告 + 确认放行"——
    /// 与 Windows 磁盘管理一致（它同样允许脱机 VHDX 宿主盘、正在使用中的磁盘），
    /// 区别是本工具会在动手前把后果说清楚（例如"系统会立即崩溃"）。
    /// 只有这个标志为 true 时，服务层才会带着已知风险继续执行，并写入日志备查。
    /// </summary>
    public bool IgnoreSafetyWarning { get; init; }

    /// <summary>进度提示（非阻塞，允许为 null）。</summary>
    public Action<string>? Report { get; init; }
}

/// <summary>
/// 磁盘级写操作。
/// 脱机/联机、只读、初始化优先走原生 IOCTL —— 这是与系统磁盘管理同一层的实现；
/// 只有在驱动不支持该控制码时才退回 Storage WMI，保证功能可用。
/// </summary>
internal static class DiskOperations
{
    /// <summary>脱机 / 联机磁盘。</summary>
    public static OperationResult SetOffline(
        int diskNumber,
        bool offline,
        bool persist = true,
        BlockResolutionOptions? options = null)
    {
        using IDisposable scope = OperationLog.Scope($"SetOffline 磁盘 {diskNumber} → {(offline ? "脱机" : "联机")}");

        // 脱机前的硬闸门：不通过就绝不发 IOCTL。
        // 系统盘被脱机时，内核不会拦（实测会直接蓝屏 0x22 FILE_SYSTEM），
        // 所以这道检查是最后也最关键的防线。
        if (offline)
        {
            DiskItem? target = GetDisk(diskNumber);
            if (target is null)
            {
                return OperationResult.Fail($"无法读取磁盘 {diskNumber} 的当前状态，为安全起见已拒绝脱机。请刷新后重试。");
            }

            OfflineGuard.Block block = OfflineGuard.Evaluate(target);
            if (block.IsBlocked)
            {
                OperationResult? gate = ApplyGate(target, block, options, "脱机");
                if (gate is not null && !gate.Success)
                {
                    return gate;
                }
            }
        }

        DiskAttribute attributes = offline ? DiskAttribute.Offline : DiskAttribute.None;
        OperationResult native = SetAttribute(diskNumber, attributes, DiskAttribute.Offline, persist, out bool nativeUnsupported);
        if (native.Success || !nativeUnsupported)
        {
            return native;
        }

        // 原生控制码不被支持时退回系统存储接口。
        OperationResult fallback = StorageOperations.SetOffline(diskNumber, offline);
        return fallback.Success ? fallback : OperationResult.Fail($"{native.Message}；{fallback.Message}");
    }

    /// <summary>
    /// 判断这块磁盘能不能安全脱机。返回失败表示不允许脱机，Message 里是原因。
    /// 具体规则集中在 <see cref="OfflineGuard"/>：系统分区、启动卷、系统/程序所在卷、
    /// 正在使用的虚拟磁盘文件、分页文件，命中任一项即拒绝。
    /// </summary>
    public static OperationResult CheckOfflineSafety(int diskNumber)
    {
        DiskItem? disk = GetDisk(diskNumber);
        if (disk is null)
        {
            return OperationResult.Fail($"无法读取磁盘 {diskNumber} 的当前状态，为安全起见已拒绝脱机。请刷新后重试。");
        }

        string? reason = OfflineGuard.GetBlockReason(disk);
        return reason is null
            ? OperationResult.Ok()
            : OperationResult.Fail($"磁盘 {diskNumber} {reason}，已拒绝脱机。");
    }

    /// <summary>设置或清除磁盘只读属性。</summary>
    public static OperationResult SetReadOnly(int diskNumber, bool readOnly)
    {
        using IDisposable scope = OperationLog.Scope(
            $"SetReadOnly 磁盘 {diskNumber} → {(readOnly ? "标记为只读" : "清除只读标记")}");

        if (readOnly)
        {
            OperationResult native = SetAttribute(diskNumber, DiskAttribute.ReadOnly, DiskAttribute.ReadOnly, true, out bool unsupported);
            if (native.Success || !unsupported)
            {
                return native;
            }

            OperationResult fallback = StorageOperations.SetDiskReadOnly(diskNumber, true);
            return fallback.Success ? fallback : OperationResult.Fail($"{native.Message}；{fallback.Message}");
        }

        // 清除只读：属性位取 0 并置掩码，不改变脱机状态。
        return SetAttribute(diskNumber, DiskAttribute.None, DiskAttribute.ReadOnly, true, out _);
    }

    /// <summary>初始化磁盘（写入空 MBR / GPT）。</summary>
    public static OperationResult Initialize(int diskNumber, PartitionStyle style)
    {
        using DiskHandle? handle = DiskHandle.TryOpen(diskNumber, writeAccess: true, out string openError);
        if (handle is null)
        {
            return OperationResult.Fail(openError);
        }

        if (handle.TryInitialize(style, out string error))
        {
            return OperationResult.Ok();
        }

        // 原生失败（例如驱动限制）时退回系统接口。
        OperationResult fallback = StorageOperations.InitializeDisk(diskNumber, style);
        return fallback.Success ? fallback : OperationResult.Fail($"{error}；{fallback.Message}");
    }

    /// <summary>转换分区表类型。</summary>
    public static OperationResult ConvertStyle(int diskNumber, PartitionStyle style)
    {
        OperationResult result = StorageOperations.ConvertStyle(diskNumber, style);
        if (result.Success && IsStyleApplied(diskNumber, style))
        {
            return OperationResult.Ok();
        }

        // 实测：某些磁盘上 ConvertStyle 会返回成功但实际什么都没做。
        // 对空磁盘来说，"清除分区表 + 按目标样式初始化"能拿到同样的结果，作为兜底。
        DiskItem? disk = GetDisk(diskNumber);
        if (disk is null)
        {
            return OperationResult.Fail($"转换后无法确认磁盘 {diskNumber} 的状态，请手动刷新查看。");
        }

        if (disk.Partitions.Count > 0)
        {
            return OperationResult.Fail(
                result.Success
                    ? "系统没有执行分区表转换。请先删除该磁盘上的所有卷，再重试。"
                    : result.Message);
        }

        OperationResult clear = StorageOperations.ClearDisk(diskNumber, zeroOut: false);
        if (!clear.Success)
        {
            return OperationResult.Fail($"清除分区表失败：{clear.Message}");
        }

        OperationResult init = Initialize(diskNumber, style);
        if (!init.Success)
        {
            return OperationResult.Fail($"重新初始化失败：{init.Message}");
        }

        WaitForDiskRefresh(diskNumber, style);
        return IsStyleApplied(diskNumber, style)
            ? OperationResult.Ok("已通过清除并重新初始化完成分区表转换。")
            : OperationResult.Fail("分区表转换未能生效，请刷新后查看。");
    }

    /// <summary>确认磁盘当前分区表是否已是指定样式。</summary>
    private static bool IsStyleApplied(int diskNumber, PartitionStyle style)
        => GetDisk(diskNumber)?.Style == style;

    /// <summary>等待系统把分区表变更反映出来。</summary>
    private static void WaitForDiskRefresh(int diskNumber, PartitionStyle style)
    {
        for (int attempt = 0; attempt < 10; attempt++)
        {
            if (IsStyleApplied(diskNumber, style))
            {
                return;
            }

            Thread.Sleep(400);
        }
    }

    /// <summary>读取单块磁盘的最新状态。</summary>
    private static DiskItem? GetDisk(int diskNumber)
        => DiskInventory.Capture().Disks.FirstOrDefault(d => d.Number == diskNumber);

    /// <summary>清除磁盘分区表。</summary>
    public static OperationResult ClearDisk(int diskNumber, bool zeroOut)
        => StorageOperations.ClearDisk(diskNumber, zeroOut);

    /// <summary>
    /// 弹出磁盘（等价于任务栏的"安全删除硬件"）。
    ///
    /// 与"脱机"的区别：脱机只是让系统不再使用该磁盘；
    /// 弹出会先卸载该磁盘上的卷、刷新写缓存，然后请求 PnP 管理器把整个设备移除，
    /// 成功后设备从系统中消失（可以安全拔线）。
    ///
    /// 只对支持安全移除的设备有效，内部固定磁盘会被直接拒绝。
    /// </summary>
    public static OperationResult Eject(int diskNumber)
    {
        using IDisposable scope = OperationLog.Scope($"Eject 磁盘 {diskNumber}");

        Dictionary<int, DiskDeviceEntry> devices = DeviceEnumerator.Enumerate();
        if (!devices.TryGetValue(diskNumber, out DiskDeviceEntry? device) || device.DevInst == 0)
        {
            return OperationResult.Fail($"找不到磁盘 {diskNumber} 对应的设备节点，无法弹出。");
        }

        if (!device.SupportsEject)
        {
            return OperationResult.Fail(
                $"磁盘 {diskNumber} 的设备不支持弹出（能力位 0x{device.Capabilities:X8}）：" +
                "内部固定磁盘没有安全移除能力，强行移除会导致数据丢失。");
        }

        DiskItem? disk = GetDisk(diskNumber);
        if (disk is not null && (disk.IsSystemDisk || disk.IsBootDisk))
        {
            return OperationResult.Fail($"磁盘 {diskNumber} 承载系统或启动卷，不能弹出。");
        }

        // 沿设备树往上找"真正具备弹出能力"的节点：
        // U 盘 / USB 硬盘盒的可弹出能力在父设备（USB 设备）上，弹磁盘节点会被判非法请求。
        uint ejectTarget = FindEjectableDevNode(device.DevInst, out string targetDescription);
        OperationLog.Write($"   弹出目标：{targetDescription}");

        /*
         * 整段"重试 + 卷脱机"放进超时保护：
         * 系统在卸载卷时可能长时间阻塞（文件被 Hyper-V 之类进程占用时尤其明显），
         * 超时就返回"仍在进行中"，不让用户界面看起来像卡死。
         */
        return RunWithTimeout(
            () => RunEjectAttempts(diskNumber, ejectTarget, targetDescription),
            EjectTimeout,
            $"磁盘 {diskNumber} 的弹出请求仍在进行中——系统正在等待卷释放，通常是该磁盘上的文件还被占用。\n\n" +
            "已停止等待。请关闭占用该磁盘的程序（例如 Hyper-V 虚拟机、资源管理器窗口）后重试；" +
            "如果弹出最终成功，设备会自行消失。");
    }

    /// <summary>真正执行弹出尝试（含把卷脱机后重试）。</summary>
    private static OperationResult RunEjectAttempts(int diskNumber, uint ejectTarget, string targetDescription)
    {
        /*
         * 弹出会被"卷正在使用"否决（否决类型 5/6，否决名是一个 STORAGE\Volume\{...} 路径）：
         * 常见原因是刚分离的 VHD 文件句柄还没释放完，或资源管理器开着那块盘上的文件夹。
         * 因此这里做有限重试，中间把该磁盘的卷强制脱机（卸载文件系统）再弹。
         */
        uint result = 0;
        NativeMethods.PnpVetoType vetoType = NativeMethods.PnpVetoType.Unknown;
        string veto = string.Empty;
        bool offlinedVolumes = false;
        int offlinedCount = 0;

        for (int attempt = 1; attempt <= EjectAttempts; attempt++)
        {
            // StringBuilder 只被原生代码写了前若干字符，其余是未初始化内存，
            // 必须按第一个 NUL 截断，否则会把残留内存当设备名显示出来。
            StringBuilder vetoName = new(260);
            result = NativeMethods.CM_Request_Device_EjectW(
                ejectTarget,
                out vetoType,
                vetoName,
                (uint)vetoName.Capacity,
                0);

            veto = SanitizeVetoName(vetoName.ToString());

            if (result == NativeMethods.CrSuccess)
            {
                return OperationResult.Ok($"设备已弹出，现在可以安全移除。（移除对象：{targetDescription}）");
            }

            if (attempt >= EjectAttempts || !IsTransientVeto(result, vetoType))
            {
                break;
            }

            if (!offlinedVolumes)
            {
                offlinedCount = VolumeDevice.TakeVolumesOffline(diskNumber, out _);
                offlinedVolumes = true;
            }

            Thread.Sleep(EjectRetryDelayMs);
        }

        string retryNote = offlinedVolumes ? $"（已先把 {offlinedCount} 个卷脱机并重试 {EjectAttempts} 次）" : string.Empty;

        return result switch
        {
            NativeMethods.CrRemoveVetoed => OperationResult.Fail(
                $"弹出被拒绝：{DescribeVeto(vetoType)}（否决类型={(int)vetoType}）{retryNote}" +
                $"{(veto.Length > 0 ? $"　{veto}" : string.Empty)}\n\n" +
                "可以关闭该磁盘上打开的文件夹/文件，或稍后再试一次。"),
            NativeMethods.CrNotDisableable => OperationResult.Fail($"磁盘 {diskNumber} 不支持弹出（设备不可禁用）。"),
            NativeMethods.CrAccessDenied => OperationResult.Fail("弹出被拒绝：权限不足，请以管理员身份运行。"),
            _ => OperationResult.Fail($"弹出失败，系统返回码 0x{result:X}{retryNote}。"),
        };
    }

    /// <summary>重试次数与间隔：给系统一点时间释放刚分离的 VHD 句柄。</summary>
    private const int EjectAttempts = 3;

    private const int EjectRetryDelayMs = 1200;

    /// <summary>
    /// 一次弹出最多等多久。
    /// 系统在卸载卷时可能长时间阻塞（例如文件被 Hyper-V 之类进程占用），
    /// 超时就返回"仍在进行中"，不让用户界面看起来像死掉。
    /// </summary>
    private static readonly TimeSpan EjectTimeout = TimeSpan.FromSeconds(12);

    /// <summary>等待后台操作完成；超时返回可读提示（操作本身仍会在后台自然结束）。</summary>
    private static OperationResult RunWithTimeout(Func<OperationResult> operation, TimeSpan timeout, string timeoutMessage)
    {
        Task<OperationResult> task = Task.Run(operation);

        if (task.Wait(timeout))
        {
            return task.Result;
        }

        return OperationResult.Fail(timeoutMessage);
    }

    /// <summary>这类否决是"暂时性"的，值得把卷脱机后再试。</summary>
    private static bool IsTransientVeto(uint result, NativeMethods.PnpVetoType vetoType)
        => result == NativeMethods.CrRemoveVetoed
            && vetoType is NativeMethods.PnpVetoType.OutstandingOpen
                or NativeMethods.PnpVetoType.Device
                or NativeMethods.PnpVetoType.PendingClose
                or NativeMethods.PnpVetoType.WindowsApp
                or NativeMethods.PnpVetoType.WindowsService;

    /// <summary>
    /// 清洗系统返回的"否决者名称"。
    ///
    /// 这个缓冲区由原生代码填写，没被写到的部分是残留内存（实测会在末尾冒出 PATH 片段
    /// 或中文乱码）。这里两道处理：先按第一个 NUL 截断，再只保留设备路径里合法出现的
    /// 字符，遇到第一个非法字符就停——保证界面上不会漏出内存垃圾。
    /// </summary>
    private static string SanitizeVetoName(string text)
    {
        int nullIndex = text.IndexOf('\0');
        string trimmed = (nullIndex >= 0 ? text[..nullIndex] : text).Trim();
        if (trimmed.Length == 0)
        {
            return string.Empty;
        }

        int end = 0;
        while (end < trimmed.Length && IsDevicePathChar(trimmed[end]))
        {
            end++;
        }

        string clean = trimmed[..end].Trim();
        return clean.Length > 96 ? clean[..96] + "…" : clean;
    }

    /// <summary>设备实例路径里允许出现的字符。</summary>
    private static bool IsDevicePathChar(char c)
        => char.IsAsciiLetterOrDigit(c)
            || c is '\\' or '&' or '#' or '{' or '}' or '-' or '.' or '_' or ':' or '=' or ',' or '(' or ')' or ' ';

    /// <summary>
    /// 从指定设备节点开始向上找第一个"具备弹出能力"的节点。
    /// 判定只看 EJECTSUPPORTED / REMOVABLE 两位——SURPRISEREMOVALOK 只代表"可以随时拔"，
    /// 不代表这个节点能接受弹出请求。
    /// </summary>
    private static uint FindEjectableDevNode(uint devInst, out string description)
    {
        uint current = devInst;

        for (int depth = 0; depth < 8; depth++)
        {
            if (HasEjectCapability(current, out uint capabilities))
            {
                description = current == devInst
                    ? $"磁盘设备节点（能力位 0x{capabilities:X8}）"
                    : $"上层设备节点（第 {depth} 层父设备，能力位 0x{capabilities:X8}）";
                return current;
            }

            uint parentResult = NativeMethods.CM_Get_Parent(out uint parent, current, 0);
            if (parentResult != 0 || parent == 0 || parent == current)
            {
                break;
            }

            current = parent;
        }

        description = "磁盘设备节点（未找到具备弹出能力的上层设备）";
        return devInst;
    }

    /// <summary>查询设备是否具备"可弹出 / 可移动"能力。</summary>
    private static bool HasEjectCapability(uint devInst, out uint capabilities)
    {
        capabilities = 0;

        byte[] buffer = new byte[4];
        uint length = (uint)buffer.Length;
        uint result = NativeMethods.CM_Get_DevNode_Registry_PropertyW(
            devInst,
            NativeMethods.CmDrpCapabilities,
            out _,
            buffer,
            ref length,
            0);

        if (result != 0 || length < 4)
        {
            return false;
        }

        capabilities = BitConverter.ToUInt32(buffer, 0);
        const uint ejectMask = NativeMethods.CmDevCapEjectSupported | NativeMethods.CmDevCapRemovable;
        return (capabilities & ejectMask) != 0;
    }

    /// <summary>
    /// 统一的"移除"入口：虚拟磁盘走分离 VHD，其他设备走弹出。
    ///
    /// 资源管理器里对 VHD 显示的"弹出"，实际动作就是分离；
    /// 直接对 VHD 发 PnP 弹出会被系统否决（PNP_VetoIllegalDeviceRequest），
    /// 所以这里按磁盘类型分派，用户只需要点同一个按钮。
    /// </summary>
    public static OperationResult RemoveDisk(DiskItem disk, BlockResolutionOptions? options = null)
    {
        using IDisposable scope = OperationLog.Scope($"RemoveDisk 磁盘 {disk.Number}（{disk.EjectActionName}）");

        // 同一套安全闸门：系统盘、启动盘、分页文件所在盘一律不许移除。
        OfflineGuard.Block block = OfflineGuard.Evaluate(disk);

        if (block.IsBlocked)
        {
            OperationResult? gate = ApplyGate(disk, block, options, disk.EjectActionName);
            if (gate is not null && !gate.Success)
            {
                return gate;
            }
        }

        if (!disk.IsVirtualDisk)
        {
            return Eject(disk.Number);
        }

        // 虚拟磁盘：优先用界面已经准备好的宿主文件路径，否则自动扫描查找。
        return options?.PreResolvedVirtualDiskFile is { Length: > 0 } file
            ? VirtualDiskOperations.Detach(disk.Number, file)
            : VirtualDiskOperations.Detach(disk.Number);
    }

    /// <summary>
    /// 尝试清掉"在用的虚拟磁盘文件"这层障碍：先卸载/分离那个虚拟磁盘。
    /// 用户不在场或拒绝时返回失败，调用方据此给出提示。
    /// </summary>
    private static OperationResult HandleInUseVirtualDiskBlock(
        DiskItem disk,
        OfflineGuard.Block block,
        BlockResolutionOptions? options)
    {
        using IDisposable scope = OperationLog.Scope($"处理在用虚拟磁盘文件 {block.Detail}");

        // 谁占着这个文件？（Restart Manager，最多等 5 秒）
        List<string> holders = TryRunWithTimeout(
            () => FileLockInspector.GetLockingProcesses([block.Detail]),
            TimeSpan.FromSeconds(5),
            out List<string> found)
            ? found
            : [];

        // 用户没有预先确认"先分离" → 只说明情况，不做任何可能阻塞的操作
        if (options?.DetachBlockingVirtualDiskConfirmed != true)
        {
            if (options?.ForceDismount == true)
            {
                // 强制模式：直接把这些卷强制脱机（Windows 磁盘管理就是这么做的），
                // 之后系统不再等待文件释放，脱机/弹出可以立即进行。
                OperationLog.Write("   强制模式：先把卷强制脱机");
                options.Report?.Invoke("正在强制卸载卷…");

                int offlined = VolumeDevice.TakeVolumesOffline(disk.Number, out int failed);
                Thread.Sleep(600);

                OperationLog.Write($"   强制脱机完成：成功 {offlined} 个，失败 {failed} 个");
                return OperationResult.Ok();
            }

            return OperationResult.Fail(
                $"磁盘 {disk.Number} 上有正在使用的虚拟磁盘文件：\n{block.Detail}\n" +
                (holders.Count > 0 ? $"\n{FileLockInspector.DescribeHolders(holders)}\n" : string.Empty) +
                "\n需要先分离它才能继续；也可以在提示里选「强制」——" +
                "那会强制卸载卷（正在使用这些文件的程序会收到错误，虚拟机可能损坏）。");
        }

        if (block.VirtualDiskNumber < 0)
        {
            return OperationResult.Fail($"无法确定 {block.Detail} 对应哪块磁盘，请在磁盘管理里手动分离。");
        }

        string file = options.PreResolvedVirtualDiskFile is { Length: > 0 } preResolved ? preResolved : block.Detail;
        options.Report?.Invoke($"正在分离虚拟磁盘 {block.VirtualDiskNumber}…");

        // 分离同样可能被卡住（文件被别的进程占着时系统会等），加超时
        if (!TryRunWithTimeout(
                () => VirtualDiskOperations.Detach(block.VirtualDiskNumber, file),
                TimeSpan.FromSeconds(20),
                out OperationResult detach))
        {
            return OperationResult.Fail(
                $"分离 {file} 超时（20 秒）：系统仍在等待文件释放。" +
                (holders.Count > 0 ? $"\n\n{FileLockInspector.DescribeHolders(holders)}" : string.Empty) +
                "\n\n如果是 Hyper-V：请确认虚拟机是「关闭」而不是「保存」。");
        }

        // 刚分离完，VHD 驱动释放宿主文件需要一点时间；
        // 立刻去弹宿主磁盘会撞上"卷正在使用"的否决，这里先等一拍。
        if (detach.Success)
        {
            Thread.Sleep(1200);
        }

        return detach;
    }

    /// <summary>带超时地执行一个后台操作；超时返回 false。</summary>
    /// <summary>
    /// 统一的安全闸门。
    ///
    /// 设计原则（按用户要求调整）：**只警告、不硬拦**。
    ///   · 用户确认"我了解风险，继续" → 放行，并把这次越权写进日志备查；
    ///   · 用户没有确认 → 返回说明（界面据此弹警告框）；
    ///   · "上有正在使用的虚拟机文件"这种情况额外提供"先分离"这条路，
    ///     用户既没确认也没选择分离时，改为强制卸载卷（与磁盘管理一致，也能避免系统卡等）。
    /// 返回 null 表示可以继续执行。
    /// </summary>
    private static OperationResult? ApplyGate(
        DiskItem disk,
        OfflineGuard.Block block,
        BlockResolutionOptions? options,
        string action)
    {
        bool ignored = options?.IgnoreSafetyWarning == true;

        if (ignored)
        {
            OperationLog.Write($"   ⚠ 安全警告已被用户确认忽略（{action}）：{block.Reason}");
        }

        if (block.Kind == OfflineGuard.BlockKind.InUseVirtualDisk)
        {
            // 用户选择"先分离那个虚拟磁盘"
            if (options?.DetachBlockingVirtualDiskConfirmed == true)
            {
                OperationResult handled = HandleInUseVirtualDiskBlock(disk, block, options);
                return handled.Success ? null : handled;
            }

            // 已确认继续但不分离 → 强制卸载卷，避免系统卡在等文件释放
            if (ignored)
            {
                OperationLog.Write("   不分离虚拟磁盘，改为强制卸载该磁盘上的卷");
                VolumeDevice.TakeVolumesOffline(disk.Number, out _);
                Thread.Sleep(600);
                return null;
            }

            return HandleInUseVirtualDiskBlock(disk, block, options);
        }

        // 其余风险（系统分区 / 启动卷 / 分页文件 / 正在运行的系统所在的宿主盘）：确认后放行
        return ignored
            ? null
            : OperationResult.Fail($"磁盘 {disk.Number} {block.Reason}，已拒绝{action}。");
    }

    /// <summary>带超时地执行一个后台操作；超时返回 false。</summary>
    private static bool TryRunWithTimeout<T>(Func<T> operation, TimeSpan timeout, out T result)
    {
        Task<T> task = Task.Run(operation);
        if (task.Wait(timeout))
        {
            result = task.Result;
            return true;
        }

        result = default!;
        return false;
    }

    /// <summary>把设备否决原因翻译成中文。</summary>
    private static string DescribeVeto(NativeMethods.PnpVetoType vetoType) => vetoType switch
    {
        NativeMethods.PnpVetoType.WindowsApp => "有应用程序正在使用该设备",
        NativeMethods.PnpVetoType.WindowsService => "有系统服务正在使用该设备",
        NativeMethods.PnpVetoType.OutstandingOpen => "还有文件句柄没有关闭",
        NativeMethods.PnpVetoType.PendingClose => "设备上有未完成的关闭操作",
        NativeMethods.PnpVetoType.IllegalDeviceRequest =>
            "系统认为该请求对这个设备不合法（该节点不提供弹出能力，通常需要移除它的上层设备）",
        NativeMethods.PnpVetoType.Device => "设备自身拒绝了请求",
        NativeMethods.PnpVetoType.Driver => "驱动程序拒绝了请求",
        NativeMethods.PnpVetoType.InsufficientPower => "电源不足",
        NativeMethods.PnpVetoType.NonDisableable => "设备不可禁用",
        NativeMethods.PnpVetoType.LegacyDevice => "旧式设备不支持",
        NativeMethods.PnpVetoType.LegacyDriver => "旧式驱动不支持",
        NativeMethods.PnpVetoType.InsufficientRights => "权限不足",
        _ => $"原因未知（类型 {(int)vetoType}）",
    };

    /// <summary>把盘符分配给卷。</summary>
    public static OperationResult AssignDriveLetter(char letter, string volumeName)
        => VolumeDevice.TryAssignDriveLetter(letter, volumeName, out string error) ? OperationResult.Ok() : OperationResult.Fail(error);

    /// <summary>删除挂载点（盘符或文件夹挂载点）。</summary>
    public static OperationResult RemoveMountPoint(string mountPoint)
        => VolumeDevice.TryRemoveMountPoint(mountPoint, out string error) ? OperationResult.Ok() : OperationResult.Fail(error);

    /// <summary>把卷装入一个空文件夹。</summary>
    public static OperationResult MountToFolder(string folderPath, string volumeName)
    {
        string normalized = folderPath.TrimEnd('\\') + "\\";
        if (normalized.Length < 4 || normalized[1] != ':')
        {
            return OperationResult.Fail($"文件夹路径无效：{folderPath}");
        }

        if (!NativeMethods.SetVolumeMountPointW(normalized, volumeName))
        {
            return OperationResult.Fail($"装入文件夹 {normalized} 失败：{Win32Error.LastMessage()}");
        }

        return OperationResult.Ok();
    }

    /// <summary>刷新系统对该磁盘的分区视图。</summary>
    public static void NotifyDiskChanged(int diskNumber)
    {
        using DiskHandle? handle = DiskHandle.TryOpen(diskNumber, writeAccess: false, out _);
        handle?.NotifyLayoutChanged();
    }

    /// <summary>按属性掩码写入磁盘属性。</summary>
    private static OperationResult SetAttribute(
        int diskNumber,
        DiskAttribute attributes,
        DiskAttribute mask,
        bool persist,
        out bool unsupported)
    {
        unsupported = false;

        using DiskHandle? handle = DiskHandle.TryOpen(diskNumber, writeAccess: true, out string openError);
        if (handle is null)
        {
            return OperationResult.Fail(openError);
        }

        if (handle.TrySetAttributes(attributes, mask, persist, out string error))
        {
            handle.NotifyLayoutChanged();
            return OperationResult.Ok();
        }

        unsupported = error.Contains("不受支持", StringComparison.Ordinal)
            || error.Contains("功能不受支持", StringComparison.Ordinal)
            || error.Contains("参数无效", StringComparison.Ordinal);

        return OperationResult.Fail(error);
    }

}
