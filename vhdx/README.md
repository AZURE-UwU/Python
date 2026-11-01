# VHDX 快照复制

一个未打包的 WinUI 3 小工具：选择 `.vhdx` 文件，选择目标文件夹，开始复制并显示进度、速度、剩余时间。

## 行为

- 源文件没有被占用：直接复制。
- 源文件正在被占用：勾选 VSS 选项时，对源文件所在卷创建 `ClientAccessible` 卷影副本，从 `\\?\GLOBALROOT\...` 路径复制。
- 复制结束后释放本次创建的卷影副本。
- 复制过程支持取消；取消或失败时，目标位置可能留下不完整文件。

## 构建

需要 Windows 10 1809 或更高版本、x64、.NET 10 SDK。
直接运行构建输出还需要 .NET 10 Desktop Runtime；下面的自包含发布命令会把运行时一起带上。

```powershell
dotnet restore
dotnet build -c Release -p:Platform=x64
```

输出在 `bin\x64\Release\net10.0-windows10.0.19041.0\win-x64\` 下。

发布为自包含目录：

```powershell
dotnet publish -c Release -r win-x64 --self-contained true -p:Platform=x64
```

## 运行

直接运行 `VhdxCopier.exe`。`app.manifest` 设置了 `requireAdministrator`，启动时会弹出 UAC；VSS 和卷影副本清理需要管理员权限。

## 限制

- 只复制选中的单个 VHDX 文件，不自动复制差分盘的父盘链。
- VSS 得到的是崩溃一致副本，不会静默 VHDX 内部的数据库或服务；需要应用一致时请先停应用或使用专门的备份软件。
- 本机 VSS 不能直接快照网络共享上的源文件；源文件在网络路径且被占用时，请用文件服务器快照或 WinPE 离线复制。
- 不创建启动项，不修改 BCD/Ventoy 菜单。
- 目标文件已存在时由用户确认覆盖。
- 程序被强制结束时，临时卷影可能残留到重启；可用管理员 PowerShell 的 `vssadmin list shadows` 查看。
