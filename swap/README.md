# 磁盘管理器（复刻系统磁盘管理 + 真机脱机/联机）

用 C# + WPF（.NET 10）写的 Windows 桌面程序，界面与操作方式对齐系统自带的「磁盘管理」，
**脱机 / 联机等操作真实作用于物理磁盘**（不是模拟）。

## 版本策略（重要）

| 程序 | 状态 | 说明 |
| --- | --- | --- |
| `DiskManagerWinUI` / `DiskOfflineToolWinUI` | **主线，后续只更新这两个** | WinUI 3 + Fluent 外观（Mica、圆角、InfoBar、ContentDialog）；依赖 Windows App Runtime（本机已装 1.8） |
| `DiskManager` / `DiskOfflineTool` | **已冻结**（2026-09-17 起停止更新） | 仅保留可用，不再加功能、不再改交互；标题栏与"关于"里都标注了「旧版 WPF · 已停止更新」。保留原因：**没有任何额外运行时依赖**，在没装 Windows App Runtime 的机器上仍可直接运行 |

> 冻结的是**界面层**：四个程序共用同一份磁盘逻辑源码（`DiskManager\Interop`、`Models`、`Services`），
> 所以底层（脱机/联机、安全闸门、VHD 分离、弹出、日志等）的修复对两版**同时生效**。
> 现在起新功能、新交互只做在 WinUI 版上。

```
swap\
├── DiskManager\            主程序（WPF 界面）
├── DiskManager.SelfTest\   自测工具（在自建 VHDX 上验证全部功能）
├── DiskOfflineTool\        小程序：只列磁盘 + 脱机/联机开关
├── DiskOfflineToolWinUI\   同上，但界面用 WinUI 3（Fluent 外观）
├── DiskManagerWinUI\       主程序的 WinUI 3 版（Fluent 外观，功能对齐 WPF 版）
└── TestDisks\              自测产生的测试盘与日志（可整目录删除）
```

---

## 一、运行（下方路径以主线 WinUI 版为准）

| 用途 | 路径 | 说明 |
| --- | --- | --- |
| 正式使用 | `DiskManager\bin\Release\net10.0-windows\win-x64\DiskManager.exe` | 清单要求管理员权限，双击后弹 UAC，同意即可 |
| 仅看界面 | `dotnet build -p:NoAdmin=true` 后的 Debug 输出 | 不提权，只能查看，写操作会提示权限不足 |
| 功能自测 | `DiskManager.SelfTest\bin\...\DiskManager.SelfTest.exe "C:\Users\AZURE\Documents\ChatGPT\swap"` | 需管理员权限；自己创建 VHDX 测试盘，不碰现成磁盘 |
| 诊断模式 | 同上，追加 `--diagnose` | 打印原生视图 / 存储接口视图 / 0 号扇区签名的对照数据 |
| 对话框线程安全回归 | 同上，追加 `--dialogs` | **不需要管理员权限**，不碰任何磁盘 |
| 改盘符端到端 | 同上，追加 `--driveletter` | 需管理员权限，在自建测试盘上跑一遍"改盘符"全流程 |

> 切换"是否需要管理员权限"的清单时，必须用 `dotnet build -t:Rebuild -p:NoAdmin=true`
> 做一次完整重建：exe 宿主(apphost)只在重建时才会重新嵌入清单，普通增量构建会保留旧清单。

---

## 一之二、磁盘脱机工具（WPF 版 · 【已冻结】）

只做两件事：列出全部磁盘、对选中的磁盘执行脱机 / 联机。适合日常只想开关磁盘的场景，
不用打开功能齐全的主程序。

| 项 | 说明 |
| --- | --- |
| 位置 | `DiskOfflineTool\bin\Release\net10.0-windows\win-x64\DiskOfflineTool.exe` |
| 界面 | 磁盘列表（磁盘 / 设备名称 / 总线 / 容量 / 分区 / 状态 / 分区表）+ 刷新、脱机、联机三个按钮 |
| 操作 | 选中磁盘后点「脱机」或「联机」，也可以直接双击列表行切换；F5 刷新 |
| 移除 | 工具栏与「操作」菜单里的按钮会**按磁盘类型自动切换动作**：<br>
        · 普通设备 → **弹出**（安全删除硬件；只对支持安全移除的设备可用）<br>
        · 虚拟磁盘 → **分离 VHD**（按钮文字随之变成"分离"，与资源管理器里对 VHD 的行为一致）<br>
        分离时先自动扫描宿主文件，找不到（目录过深等）会弹出文件选择框让你手动指定，
        并校验该文件确实对应这块磁盘后才执行 |
| 挡路可解 | 如果要弹出的磁盘上放着**正在使用的虚拟磁盘文件**（例如 USB SSD 上存着某个已挂载 VHD 的 .vhdx），
        程序不会只丢一句拒绝，而是问你要不要**先分离那个虚拟磁盘、然后继续弹出**；
        脱机遇到同样情况也是这个流程 |
| 自动刷新 | 两个程序都监听系统硬件变化消息（`WM_DEVICECHANGE`）：插拔 U 盘/移动硬盘、挂载或分离 VHD、
        在资源管理器里弹出设备，列表都会自动更新，不用手动按 F5。
        事件会成串到达，做 0.7 秒防抖；如果此刻正在执行操作，会把刷新延后再做，不会丢事件。
        另外弹出/分离这类异步操作结束后，除了立刻刷新，还会在 1.2~1.5 秒后补刷一次——
        设备从系统里摘除需要一点时间，刷太早会把刚移除的磁盘又列出来 |
| 保护 | 系统盘与启动盘禁止脱机（按钮自动置灰并给出说明）；脱机前有确认框；脱机不会删除数据 |
| 权限 | 需要管理员权限；未提权时显示黄色提示条，只能查看 |
| 自检 | `DiskOfflineTool.exe <工作目录> --selftest`（需管理员权限，只在自建 VHDX 上操作） |

自检结果（2026-09-17）：**11 项全部通过** —— 列磁盘、发现测试盘、三重安全锁、脱机成功、
列表显示"脱机"、联机成功、列表恢复、系统盘不可脱机保护。日志：`TestDisks\offline-tool-selftest.log`。

---

## 一之三、磁盘脱机工具 · WinUI 3 版（现代化外观）

功能与上面那个小工具完全一致，界面换成 **WinUI 3（Windows App SDK）**，用于获得 Fluent 外观。

| 项 | 说明 |
| --- | --- |
| 位置 | `DiskOfflineToolWinUI\bin\Release\net10.0-windows10.0.19041.0\DiskOfflineToolWinUI.exe` |
| 外观 | Mica 背景、Fluent 标题排版、圆角卡片、**InfoBar** 权限提示、**ContentDialog** 确认框、Segoe UI Variable |
| 逻辑 | 与 WPF 版**共用同一份磁盘代码**（链接 `DiskManager\Interop` 与 `Services`），只重写了界面层 |
| 部署 | 非打包（unpackaged）：双击 exe 即可，不需要 MSIX。本机已装 Windows App Runtime 1.8，所以用框架依赖模式 |
| 自动刷新 | 用 WinRT 的 `DeviceInformation.CreateWatcher(磁盘接口类 GUID)` 监听插拔，配合操作后的延迟补刷 |
| 布局 | 选中项信息放在按钮**下方独立一行**（带底色的圆角卡片），窗口变窄时**自动换行**，不会像原来那样被截断；
        初始窗口尺寸按逻辑像素设置并自动换算 DPI 缩放（高 DPI 下不会偏小） |
| 权限 | 清单为 `requireAdministrator`（与 WPF 版一致，双击弹 UAC） |
| 只验证界面 | `dotnet build -t:Rebuild -p:NoAdmin=true`（会改用 asInvoker 清单） |

换到没装 Windows App Runtime 的机器上：把 csproj 里的
`<WindowsAppSDKSelfContained>` 改成 `true`（约 +80 MB，自带运行时，免安装）。

---

## 一之四、主程序 · WinUI 3 版（主线）

完整功能的主程序，界面换成 WinUI 3，与 WPF 版**行为一致**（共用同一份磁盘逻辑）。

| 项 | 说明 |
| --- | --- |
| 位置 | `DiskManagerWinUI\bin\Release\net10.0-windows10.0.19041.0\DiskManagerWinUI.exe` |
| 界面 | 菜单栏（文件/操作/查看/帮助）、InfoBar 权限提示、卷列表（11 列，窄窗口横向滚动）、
        **可拖动的分隔条**、下方磁盘图（按容量比例绘制分区与未分配空间）、状态栏 |
| 菜单 | 卷：打开 / 更改驱动器号和路径 / 格式化 / 扩展卷 / 压缩卷 / 删除卷 / 属性<br>
        磁盘：初始化 / 新建简单卷 / 脱机·联机 / 只读 / 弹出·分离 VHD / 转换 MBR·GPT / 清除磁盘 / 属性<br>
        右键：卷列表行、磁盘图上的分区块与未分配区域（后两者显示磁盘菜单） |
| 对话框 | 全部改用 WinUI 的 **ContentDialog**：初始化、新建简单卷、格式化、更改驱动器号、
        扩展、压缩、属性、危险操作确认（含"我已确认"复选框） |
| 自动刷新 | 与 WinUI 小工具相同的 `DeviceWatcher` + 操作后延迟补刷 |
| 调试 | `DiskManagerWinUI.exe` 加 `demo` 参数 → 用内置假数据填充界面（无需提权，方便调 UI）<br>
        `dotnet build -t:Rebuild -p:NoAdmin=true` → asInvoker 清单，只验证界面 |

> 注意：**XML 注释里不能出现连续两个减号**。调试清单注释里写了命令行开关，导致清单不是合法
> XML，程序启动时报"应用程序的并行配置不正确"——这个坑已记在案，改注释即可。

> 所有写操作（含读磁盘句柄）都需要管理员权限：实测非管理员连 `\\.\PhysicalDrive0` 都打不开（错误 5）。

---

## 二、功能对照表

| 功能 | 实现方式 | 状态 |
| --- | --- | --- |
| 卷列表（卷/布局/类型/文件系统/状态/容量/可用空间/% 可用/容错/开销/磁盘） | 原生 IOCTL + 卷 API | 可用 |
| 磁盘图（按容量比例画分区、未分配空间、选中高亮、右键菜单） | 自绘控件 `DiskMapView` | 可用 |
| **脱机 / 联机** | `IOCTL_DISK_SET_DISK_ATTRIBUTES`（原生），失败时退回存储接口 | 可用 |
| 只读 / 读写切换 | 同上（`DISK_ATTRIBUTE_READ_ONLY`） | 可用 |
| 初始化磁盘（MBR / GPT） | `IOCTL_DISK_CREATE_DISK`（原生），失败时退回存储接口 | 可用 |
| 转换为 MBR / GPT | 存储接口 + 结果校验 + 空盘兜底重建 | 可用 |
| 新建简单卷（大小/盘符/文件系统/卷标/快速格式化） | 存储接口建分区 → 分配盘符 → 格式化（`format.com` 兜底） | 可用 |
| 删除卷 | 存储接口 `DeleteObject` | 可用 |
| 格式化（NTFS/exFAT/FAT32/ReFS、分配单元大小） | 存储接口 `Format` | 可用 |
| 更改驱动器号和路径（分配 / 删除 / 装入 NTFS 文件夹） | 存储接口 `AddAccessPath` + 原生挂载点接口 | 可用 |
| 扩展卷 / 压缩卷 | 存储接口 `GetSupportedSize` + `Resize` | 可用 |
| 清除磁盘、属性对话框、刷新（F5）、顶部/底部窗格切换 | — | 可用 |

**已知限制**：动态磁盘（带区/镜像/RAID-5）在"布局"列统一显示为「跨区」——系统未提供公开接口区分；
需要识别请改用 VDS。自动刷新未实现，磁盘热插拔后按 F5。

---

## 三、代码结构

```
DiskManager\
├── Interop\                 原生互操作层（P/Invoke，全部集中在此，便于核对签名）
│   ├── NativeConstants.cs   IOCTL 控制码、错误码中文翻译
│   ├── NativeStructs.cs     与 Windows SDK 逐字节对齐的结构体
│   ├── NativeMethods.cs     所有 DllImport 声明
│   ├── DiskIo.cs            DeviceIoControl / ReadFile 的统一封装
│   ├── DiskHandle.cs        物理磁盘句柄：脱机联机、分区表、初始化、0 号扇区签名
│   ├── VolumeDevice.cs      卷枚举、卷区间、挂载点增删
│   └── DeviceEnumerator.cs  SetupDi 枚举设备，取"设备友好名"
├── Models\                  界面模型（INotifyPropertyChanged）
├── Services\
│   ├── DiskInventory.cs     枚举并组装成界面模型（全程只读）
│   ├── DiskOperations.cs    磁盘级写操作（优先原生 IOCTL）
│   ├── StorageOperations.cs 卷/分区级写操作（Storage WMI）
│   ├── VolumeOperations.cs  多步骤流程：建卷→分配盘符→格式化、扩展/压缩等
│   └── Elevation.cs         权限检测与提权重启
├── Controls\DiskMapView.cs  磁盘图自绘控件
├── Dialogs\                 确认、初始化、新建卷、格式化、改盘符、扩展、压缩、属性
└── MainWindow.xaml(.cs)     菜单、卷列表、磁盘图、右键菜单、状态栏
```

---

## 四、踩坑记录（这些都是实测踩出来的，别再改回去）

1. **IOCTL 常量必须现场计算或查头文件**
   `IOCTL_DISK_GET_DISK_ATTRIBUTES / SET_DISK_ATTRIBUTES` 是 function 0x3c / 0x3d（不是早期文档里的 0x36 / 0x37），
   本项目所有控制码都以 SDK `winioctl.h` 为准核对过。

2. **结构体里的 union 必须用显式布局**
   `PARTITION_INFORMATION_EX` 是 144 字节、`DRIVE_LAYOUT_INFORMATION_EX` 头部是 48 字节、`CREATE_DISK` 是 24 字节；
   顺序布局会把 union 排成前后两个字段，数据全错位。

3. **`VOLUME_DISK_EXTENTS` 里的分区数组偏移是 8，不是 4**
   结构里 `LARGE_INTEGER` 让 `DISK_EXTENT` 按 8 字节对齐，数组前面有 4 字节填充。
   按偏移 4 解析会把所有卷的"磁盘号"读成 0、偏移整体错位——现象是"扩展卷/压缩卷找不到分区"。

4. **`MSFT_Disk.CreatePartition` 不要同时要盘符**
   带 `AssignDriveLetter=true` 实测返回"拒绝访问"(5)；不带该参数一定成功。
   正确顺序是：建分区 → `AddAccessPath` 分配盘符 → 格式化。

5. **`MSFT_Partition.AddAccessPath` 不要传 `AssignDriveLetter`**
   指定了 `AccessPath` 再传 `AssignDriveLetter=false` 会返回"拒绝访问"(5)；
   不传这个参数才会按 AccessPath 建立挂载点（系统自带 `Add-PartitionAccessPath` 就是这么调的）。

6. **给已有卷换盘符要先摘掉旧盘符**
   直接加新盘符会被拒；顺序必须是"删旧 → 加新"（系统自带 `Set-Partition -NewDriveLetter` 同样如此）。

7. **`MSFT_Disk.ConvertStyle` 可能返回成功但什么都没做**
   所以转换后必须校验实际分区表；对空盘用"清除分区表 + 按目标样式初始化"兜底。

8. **MBR 磁盘上建分区不能带 `IsHidden/IsActive/IsGPT` 等参数**
   否则返回 `0xa02e`：「The parameters GptType, IsHidden, and IsDAX cannot be used on an MBR disk」。

9. **"未初始化"要靠 0 号扇区的 0x55AA 判断**
   底层控制码对未初始化磁盘会报告成「MBR + 0 分区」，只看样式会误判；本项目读 0 号扇区确认，
   与磁盘管理的显示一致。

10. **本机 diskpart 的 vdisk 命令不可用**：Virtual Disk Service(vds) 处于停止状态，
    `diskpart /s 脚本` 会静默失败且退出码仍是 0。自测改用 `New-VHD / Mount-VHD / Dismount-VHD`。

11. **PowerShell 脚本文件必须带 BOM**：无 BOM 的 UTF-8 被 PowerShell 5.1 按本地代码页解析，
    中文注释会把换行"吃掉"，导致后续命令行被当成注释——现象是脚本"静默少执行几行"。

12. **对话框的结果属性不能"现读控件"**：写操作跑在后台线程上，如果在后台线程里读 `TextBox.Text` /
    `CheckBox.IsChecked`，WPF 会抛「调用线程无法访问此对象，因为另一个线程拥有该对象」（用户实测踩到过）。
    本项目所有对话框的返回值都在点"确定"时（界面线程）拍成纯数据快照属性，`MainWindow` 也再在界面线程
    取一次局部变量后才交给后台线程。回归测试：`SelfTest.exe <工作目录> --dialogs`。

13. **内核不阻止脱机系统盘，脱了就是蓝屏 0x22**：把 Windows 正在使用的磁盘（系统分区 / 启动卷 /
    承载分页文件的磁盘）脱机，系统会当场失去自己的文件系统，直接 `0x22 FILE_SYSTEM` 崩溃。
    磁盘管理之所以看起来安全，是**软件层把按钮灰掉了**，而不是驱动层有保护——
    实测直接发 `IOCTL_DISK_SET_DISK_ATTRIBUTES` 能够把系统盘脱机。因此本项目在
    **执行层**加了硬闸门 `DiskOperations.CheckOfflineSafety()`：脱机前统一检查
    ①系统分区 ②启动卷 ③本程序运行目录所在卷 ④分页文件所在磁盘，任一命中直接拒绝，
    界面置灰只是第一道防线，不再是唯一的防线。

14. **承载"正在使用的虚拟磁盘文件"的磁盘也必须拒绝脱机**：本机 Windows 就是从
    `A:\VHDX\USED\ENG\Win11ProW X64.vhdx`（430 GB，磁盘 0 上）启动的——
    把磁盘 0 脱机 = 抽走系统自己的 VHDX 宿主盘 = 立刻蓝屏 0x22。判定方法见
    `Services/OfflineGuard.cs`：在目标磁盘的各卷上按有限深度（≤4 层、每卷 ≤600 个目录）
    找 `.vhd/.vhdx`，再**用独占方式试打开**（`FileShare.None`）——能独占打开说明没人用，
    打不开说明 VHD 驱动或别的程序正拿着它，即判定为"不可脱机"。同时把结论写进
    `DiskItem.OfflineBlockReason`，让界面**提前置灰并说明原因**，而不是等用户点了才拒绝。
    本机实测结果：磁盘 0 拒绝（在用 VHDX）、磁盘 1 允许、磁盘 2 拒绝（系统分区）、磁盘 3 允许。

15. **SetupDi 明细里的设备路径偏移不能写死**：普遍写法是"64 位从偏移 8 读字符串"，
    但实测 Windows 11 26100 把字符串写在偏移 4，按 8 读会吃掉开头的两个反斜杠，
    得到 `?\scsi#disk&...` 这种非法路径 → `CreateFile` 报错 123 → **整个设备枚举静默变空**。
    现象是设备名一直走备用来源、能力位永远是 0、弹出按钮永远灰着。现在改为扫描前 64 字节
    定位 UTF-16 的 `\\` 起点，不依赖结构体打包方式。

16. **`CM_Get_DevNode_Registry_Property` 的 `pulLength` 是 in/out 参数**：必须先把缓冲区
    字节数写进去，否则系统读到的是未初始化长度，会随机返回 `CR_BUFFER_SMALL (0x1A)`——
    同一个调用"有时成功、有时读回 0"。另外 `CM_DRP_*` 与 `SPDRP_*` 的值差 1：
    `CM_DRP_CAPABILITIES = 0x10`（写成 0x0F 会读到"物理设备对象名"字符串）。

17. **判断"能否弹出"要看的位不止一个**：`CM_DEVCAP_EJECTSUPPORTED(0x2)`、
    `CM_DEVCAP_REMOVABLE(0x4)`、`CM_DEVCAP_SURPRISEREMOVALOK(0x80)` 任一命中即可。
    本机实测：USB SSD 只报 `0xE0`（含"可随时移除"），内部 NVMe 报 `0x60`（既不可弹出也不可移动），
    只看"可弹出"那一位会把 USB 盘误判成不能弹出。

18. **挂载中的 VHD 不能靠 PnP 弹出**：它报告支持弹出（`0x62`），但 `CM_Request_Device_Eject`
    会以 `PNP_VetoIllegalDeviceRequest (8)` 否决——正解是用磁盘管理里的「分离 VHD」。
    工具在这种情况下会回一句可读的否决原因，也不会对磁盘造成任何影响。

19. **虚拟磁盘要用 virtdisk.dll 的分离接口**：`OpenVirtualDisk` / `GetVirtualDiskPhysicalPath`
    / `DetachVirtualDisk`。三个细节：① 导出名**不带 W 后缀**（这个 API 只有 Unicode 版本，
    写 `OpenVirtualDiskW` 会 EntryPointNotFound）；② `GetVirtualDiskPhysicalPath` 的
    `diskPathSizeInBytes` 同样是 in/out，要预填缓冲区字节数；③ 打开已挂载的 VHD 用
    `VIRTUAL_DISK_ACCESS_GET_INFO`，分离用 `DETACH | GET_INFO`，失败时退回 `NONE`。
    验证方式（不需要管理员）：对候选文件调用这两个接口，能拿到 `\\.\PhysicalDriveN`
    就说明它对应哪块磁盘。实测 `D:\Win11ProW X64.vhdx → \\.\PhysicalDrive4` 正确。

20. **自动找宿主文件是有界的**：扫描深度 ≤8 层、每卷 ≤600 个目录，超出范围的深目录会漏掉
    （例如 `C:\Users\...\Documents\ChatGPT\swap\TestDisks\x.vhdx` 就被 `C:\Windows` 等其他目录
    耗尽了配额）。所以分离流程提供**手动指定文件**的兜底，并校验文件与磁盘的对应关系。
    另外 `Get-DiskImage -DevicePath` 与 `MSFT_DiskImage` 都不可靠：前者需要管理员，
    后者在这台机器上枚举为空。

21. **弹出会被"卷正在使用"否决，需要重试 + 先把卷脱机**：分离掉挡路的 VHD 之后立刻弹宿主磁盘，
    系统会返回 `CR_REMOVE_VETOED` + 否决类型 5/6（否决者名称是一个 `STORAGE\Volume\{...}` 路径）——
    刚分离的 VHD 文件句柄还没释放完，或者资源管理器开着那块盘上的文件夹。
    现在处理成：分离成功后**等待 1.5 秒**，弹出失败时**把该磁盘上的卷强制脱机**
    （`IOCTL_VOLUME_OFFLINE`，等价于磁盘管理的"脱机卷"）再重试，最多 3 次、每次间隔 1.2 秒；
    仍失败才报错，并在消息里说明"关闭该磁盘上打开的文件夹/文件"。

22. **原生缓冲区里的字符串必须清洗**：`CM_Request_Device_Eject` 返回的否决者名称是写进我们提供的
    缓冲区里的，**没被写到的部分是残留内存**——实测末尾会冒出 PATH 片段甚至是中文乱码。
    处理办法：先按第一个 NUL 截断，再只保留设备路径里合法出现的字符，遇到第一个非法字符就停；
    超过 96 字符再截断加省略号。

23. **窄窗口下要有横向滚动，不能裁内容**：
    · WinUI 版磁盘列表：把表头放进 `ListView.Header`，让表头与数据行共用同一个滚动容器，
      行内容设 `MinWidth`（880），窗口更窄时出现横向滚动条、两者一起滑动；
      另外窗口尺寸变化后 `ScrollViewer` 会保留旧的横向偏移，需要显式复位到最左端。
    · WPF 主程序的磁盘图：`DiskMapView` 原来把横向滚动设成了 `Disabled`，窗口一窄分区就被裁掉；
     现在改为 `Auto`，并给每行设 `MinWidth`（左栏 + 520），窄窗口下可以左右滑动查看。

24. **XML 注释里不能出现连续两个减号**：调试清单的注释里写了命令行开关，导致清单不是合法
    XML，程序启动直接报"应用程序的并行配置不正确"。改注释即可。

25. **被占用时要告诉用户"是谁占的"，并且不要傻等**：
    · 用 **Windows Restart Manager**（`rstrtmgr.dll`）查文件被哪些进程占用——
      它正是安装程序判断"文件被占用无法覆盖"用的接口，实测能准确报出
      `System (PID 4)`（VHD 驱动挂在系统进程里）这类占用者；
    · Hyper-V 场景最典型：关掉虚拟机后 VHDX 常仍被 `vmwp.exe` / System 持有，
      尤其是虚拟机处于"已保存"而不是"已关闭"时。原来只拒绝脱机、不说明占用者，
      用户只知道"被占用"却不知道找谁；现在一旦查出占用者就**立即返回**，
      连弹出请求都不发（那正是卡死的来源）。
    · **弹出/分离必须加超时**：系统卸载卷时会一直等文件释放，
      `CM_Request_Device_Eject` 可能阻塞几十秒以上，用户看到的就是"卡死"。
      现在弹出总超时 12 秒、分离超时 15 秒，超时立刻返回可读提示，
      后台请求让它自然结束——界面不再像死掉。

26. **"卡死"的两个真正来源（都要堵）**：
    · **界面层必须给整个操作加硬超时**：之前只给"弹出请求"那一小步加了超时，
      但真正会无限等待的是它**前面**的枚举/扫描——遍历所有卷、逐目录扫描、逐个试开文件，
      这些 I/O 打在一块响应很慢或半挂起的 USB 盘上会一直等下去，任何内部超时都兜不到。
      现在四个程序的界面层都包了 15 秒（状态评估）/ 40 秒（整次操作）硬超时，
      等待期间状态栏每秒显示"已等待 N 秒…"，用户能看出程序还活着。
    · **绝不让后台线程回调界面**：后台线程要弹确认框只能"投递到界面线程 + 阻塞等待"，
      而界面线程可能正忙于处理对话框本身 → 互相等待形成死锁，
      **这时任何 UI 超时都救不回来**（界面线程自己就卡住了）。
      改成：所有用户交互（确认、选文件）都在操作**之前**、在界面线程上正常 await 完成，
      服务层只接收"已确认 + 宿主文件路径"这样的结果，运行期不再有任何界面回调。
    · **诊断**：每一步都写进 `%LOCALAPPDATA%\DiskTools\operations.log`（进入/离开 + 耗时），
      万一还卡住，看最后一条"进入但没离开"的记录就知道卡在哪一步。

27. **虚拟机在用的文件不止 .vhdx**：安全闸门原来只找 `.vhd/.vhdx`，
    而 Hyper-V 还会占用检查点（`.avhdx`）、配置（`.vmcx`）、保存状态（`.vmrs`/`.vsv`）、
    内存直通盘（`.vhdpmem`）等。漏掉这些 → 闸门误判"可以用" → 系统去卸载卷时
    卡在等这些文件释放（**这就是"弹出卡死"的根因**，日志里表现为：
   闸门 2 ms 放行，之后没有任何记录）。
    现在扫描范围扩到虚拟机专属类型；**刻意不含 `.iso`/`.bin`** ——
    实测它们会把游戏 `.bin`、系统镜像误判成虚拟机文件，导致正常数据盘也被拒绝脱机。

28. **每一层操作都要有日志作用域**：只给"弹出请求"加日志不够——
    `SetOffline`（脱机 IOCTL）、`Eject`（枚举设备 / 找弹出目标 / 卷脱机重试）
    都补上了 `OperationLog.Scope`。日志里出现"→ 某步骤"却没有对应的"←"，
    就是卡在这一步。

29. **为什么 Windows 磁盘管理"轻而易举"，而本工具会拦**：
    磁盘管理**不检查文件占用**，直接强制卸载卷（正在使用这些文件的程序会收到句柄失效错误），
    而且它**不检查该盘是不是某个 VHDX 的宿主**——所以它能把正在运行的系统所在的 VHDX 宿主盘脱机，
    直接导致蓝屏 0x22（本机实测过）。本工具的闸门是**故意**加的。
    为了让用户仍能获得磁盘管理那样的能力，现在提供**强制通道**：
    三选一提示里的「强制卸载」= 先把该磁盘上的卷强制脱机（`IOCTL_VOLUME_OFFLINE`）
    再执行脱机/弹出——这正是磁盘管理的动作，也因此不会再等待文件释放（不会卡）。
    代价写在提示里：正在使用这些文件的程序会出错，虚拟机磁盘可能损坏。

30. **安全闸门改为"只警告、不硬拦"（按用户要求）**：
    · 原来四种风险直接拒绝执行：系统分区、启动卷、分页文件、上有正在使用的虚拟机文件；
      现在一律**弹警告说明后果**，用户点「继续执行」就照做（与 Windows 磁盘管理一致），
      越权操作写进 `operations.log` 备查；
    · 界面上**不再因为安全闸门把按钮置灰** —— 可用性只按"能不能做"决定
      （例如已脱机就只能联机），风险交给点击后的警告框；
    · 唯一保留的是**把后果写清楚**：例如「该磁盘含启动卷 → 执行后系统会立即崩溃（蓝屏 0x22）」，
      这正是本机实测过的场景，属于"告知"而不是"限制"；
    · 虚拟机文件那种情况额外保留一条推荐路径：「先分离并继续」；
      用户选择直接继续但不分离时，代码会先把卷强制卸载，避免系统卡在等文件释放。
    · 注意：两个 **WPF 版已冻结**，UI 保持原样（仍是拦截）；
      新行为只在 WinUI 版（`DiskOfflineToolWinUI` / `DiskManagerWinUI`）生效。

---

## 五、自测结果（2026-09-17，Windows 11 26100）

自测工具在自建的 2 GB VHDX 上跑完整流程，**37 项检查全部通过**：

```
1  初始状态识别（未初始化 / 联机）
2  原生 IOCTL 脱机 → 属性位确认 → 联机 → 确认恢复
3  只读标记 / 清除只读
4  初始化 GPT
5  新建简单卷（512 MB / NTFS / 卷标 / 盘符）
6  在新卷上写入并读回文件
7  扩展卷 512 MB → 768 MB
8  压缩卷 768 MB → 640 MB
9  更改驱动器号、删除驱动器号
10 卷在脱机磁盘上的表现
11 删除卷
12 转换为 MBR 分区表
13 清除磁盘
14 MBR 磁盘上再建卷（覆盖另一条代码路径）
15 未分配空间统计
```

安全约束（写死在自测程序里）：只操作"测试前不存在、且总线类型为虚拟磁盘、容量与设定值一致"的那块磁盘；
三条不满足任意一条立即中止，不做任何写操作。测试日志见 `TestDisks\selftest.log`。

清理：`TestDisks` 目录（含 `BACKUP\DEL` 里的历史测试盘）共约 0.5 GB，确认不需要后可以整个删除。
