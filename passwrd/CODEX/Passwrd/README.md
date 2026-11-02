# Passwrd

Passwrd 是一个使用 WinUI 3 和 XAML 编写的 Windows 快捷输入工具。按下全局快捷键后，程序通过 `SendInput` 将配置的 Unicode 文本输入到当前获得焦点的窗口。

## 功能

- 管理多条快捷输入配置。
- 使用 Ctrl、Alt、Shift、Win 与常用按键组合成全局快捷键。
- 支持中文、换行、制表符和输入后自动按 Enter。
- 关闭主窗口后隐藏到系统托盘，双击托盘图标恢复窗口。
- 托盘菜单可打开设置、暂停或启用全局快捷键、退出程序。
- 配置自动保存，首次启动立即展示示例输入。
- 单实例运行，重复启动会唤回已有窗口。
- 跟随 Windows 的系统深浅色主题。
- 使用独立的键帽图标，应用于程序、标题栏和系统托盘。

## 运行

直接运行发布目录中的 `Passwrd.exe`。发布版本包含 .NET 与 Windows App SDK 运行文件，无需另装开发环境。

配置保存在：

```text
%LOCALAPPDATA%\Passwrd\settings.json
```

配置使用普通 JSON 文本存储。此版本不面向密码或其他敏感内容。

## 构建

在项目目录执行：

```powershell
dotnet publish .\Passwrd.csproj -c Release -r win-x64 --self-contained true -o .\dist
```

## 使用限制

- Windows 保留或其他程序已占用的热键组合无法注册，界面会显示冲突原因。
- 设置窗口获得焦点时不会执行模拟输入，避免文本误输入到设置界面。
- 模拟输入会发送到快捷键触发时的前台窗口；部分以管理员权限运行的应用可能拒绝普通权限进程的键盘输入。
