/// @file    Program.cs
/// @author  Bowen
/// @module  ADBDevTool / adb 输出解析自检
///
/// 本程序只验证 ADBDevTool.Services.AdbService 的纯解析逻辑，不接触真机与网络。

using System;
using System.Collections.Generic;
using System.Linq;
using ADBDevTool.Services;

var failures = 0;

void Check(string name, bool condition, string? detail = null)
{
    Console.WriteLine(condition ? $"PASS  {name}" : $"FAIL  {name}{(detail is null ? string.Empty : "  → " + detail)}");
    if (!condition)
    {
        failures++;
    }
}

// 1. mdns：制表符分隔的真实格式
var mdnsTabs = "List of discovered mdns services\n"
    + "adb-3D4E5F6G-1a2B3c\t_adb-tls-connect._tcp\t192.168.1.20:37787\n"
    + "adb-3D4E5F6G-9z8Y7x\t_adb-tls-pairing._tcp\t192.168.1.20:41235\n"
    + "adb-7H8I9J0K-4d5E6f\t_adb-tls-connect._tcp\t192.168.1.33:45678\n";

var services = AdbService.ParseMdnsServices(mdnsTabs);
Check("mdns 解析出 3 条服务", services.Count == 3, $"实际 {services.Count}");
Check("识别 2 个连接端口", services.Count(s => s.Kind == MdnsServiceKind.Connect) == 2);
Check("识别 1 个配对端口", services.Count(s => s.Kind == MdnsServiceKind.Pairing) == 1);
Check(
    "配对端口解析正确",
    services.First(s => s.Kind == MdnsServiceKind.Pairing).Endpoint == "192.168.1.20:41235");
Check(
    "服务名保留",
    services.First(s => s.Kind == MdnsServiceKind.Connect).Name == "adb-3D4E5F6G-1a2B3c");

// 2. mdns：空白分隔的退化格式
var mdnsSpaces = "List of discovered mdns services\n"
    + "adb-XXXX-YYYY _adb-tls-connect._tcp 10.0.0.8:37001\n";
Check("空白分隔格式可解析", AdbService.ParseMdnsServices(mdnsSpaces).Count == 1);

// 3. mdns：空结果
Check("空结果不产生条目", AdbService.ParseMdnsServices("List of discovered mdns services\n").Count == 0);

// 4. devices -l
var devicesText = "List of devices attached\n"
    + "192.168.1.20:37787\tdevice product:raven model:Pixel_6 device:raven transport_id:3\n"
    + "192.168.1.33:45678\tunauthorized transport_id:4\n"
    + "R58M12ABCDEF\tdevice product:beyond1 model:SM_G9730 transport_id:1\n"
    + "192.168.1.44:5555\toffline transport_id:5\n";

var devices = AdbService.ParseDevices(devicesText);
Check("devices 解析出 4 台", devices.Count == 4, $"实际 {devices.Count}");

var pixel = devices.First(d => d.Serial == "192.168.1.20:37787");
Check("已连接状态识别", pixel.State == AdbDeviceState.Connected);
Check("型号解析", pixel.Model == "Pixel_6");
Check("显示名去下划线", pixel.DisplayName == "Pixel 6");
Check("无线设备标记", pixel.IsWireless);

var unauthorized = devices.First(d => d.Serial == "192.168.1.33:45678");
Check("待授权状态识别", unauthorized.State == AdbDeviceState.Unauthorized);
Check("待授权无型号时用默认名", unauthorized.DisplayName == "未命名设备");
Check("待授权文案", unauthorized.StateText == "待授权");

var usb = devices.First(d => d.Serial == "R58M12ABCDEF");
Check("USB 设备不是无线", !usb.IsWireless);
Check("USB 型号解析", usb.Model == "SM_G9730");

Check(
    "离线状态识别",
    devices.First(d => d.Serial == "192.168.1.44:5555").State == AdbDeviceState.Offline);

// 5. 地址校验
Check("合法地址通过", AdbService.TryNormalizeEndpoint(" 192.168.1.20:37787 ", out var normalized, out _)
    && normalized == "192.168.1.20:37787");
Check("缺端口被拒", !AdbService.TryNormalizeEndpoint("192.168.1.20", out _, out _));
Check("端口越界被拒", !AdbService.TryNormalizeEndpoint("192.168.1.20:70000", out _, out _));
Check("端口为 0 被拒", !AdbService.TryNormalizeEndpoint("192.168.1.20:0", out _, out _));
Check("空输入被拒", !AdbService.TryNormalizeEndpoint("   ", out _, out _));

// 6. mDNS 服务名形态的 serial 归并（真机现象：同一台手机会同时出现 IP:端口 与 mDNS 名两条记录）
var endpointByServiceName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
{
    ["adb-fa34ffbe-oXIr6O"] = "10.42.0.131:36549",
};

Check(
    "mDNS 名还原成 IP:端口",
    AdbService.CanonicalizeSerial("adb-fa34ffbe-oXIr6O._adb-tls-connect._tcp", endpointByServiceName)
        == "10.42.0.131:36549");
Check(
    "IP:端口 形态保持不变",
    AdbService.CanonicalizeSerial("10.42.0.131:36549", endpointByServiceName) == "10.42.0.131:36549");
Check(
    "查不到服务时保持原样",
    AdbService.CanonicalizeSerial("adb-unknown-zzz._adb-tls-connect._tcp", endpointByServiceName)
        == "adb-unknown-zzz._adb-tls-connect._tcp");
Check(
    "USB 序号不变",
    AdbService.CanonicalizeSerial("R58M12ABCDEF", endpointByServiceName) == "R58M12ABCDEF");

var mdnsFormUsb = AdbService.ParseDevices(
    "List of devices attached\nadb-fa34ffbe-oXIr6O._adb-tls-connect._tcp\tdevice model:24129PN74C transport_id:7\n");
Check("mDNS 名形态算无线设备", mdnsFormUsb.Count == 1 && mdnsFormUsb[0].IsWireless);

// 7. 可断开目标筛选
Check(
    "两种无线形态都可断开",
    AdbService.DisconnectTargetsFor(new[] { "10.42.0.131:36549", "adb-fa34ffbe-oXIr6O._adb-tls-connect._tcp" }).Count == 2);
Check(
    "USB 序号不可断开",
    AdbService.DisconnectTargetsFor(new[] { "R58M12ABCDEF" }).Count == 0);

// 8. 快捷键描述的格式化与解析（存进 settings.json 的就是这里的文本形式）
Check(
    "格式化 修饰键+字母",
    new HotkeySpec(HotkeySpec.ModControl | HotkeySpec.ModAlt, 0x4D).ToString() == "Ctrl+Alt+M");
Check(
    "格式化 顺序为 Ctrl/Alt/Shift/Win",
    new HotkeySpec(
        HotkeySpec.ModWin | HotkeySpec.ModShift | HotkeySpec.ModAlt | HotkeySpec.ModControl,
        0x4D).ToString() == "Ctrl+Alt+Shift+Win+M");
Check("格式化 功能键", new HotkeySpec(HotkeySpec.ModControl, 0x74).ToString() == "Ctrl+F5");
Check("格式化 具名键", new HotkeySpec(HotkeySpec.ModAlt, 0x0D).ToString() == "Alt+Enter");

Check("解析默认组合", HotkeySpec.TryParse("Ctrl+Alt+M", out var parsed)
    && parsed.Modifiers == (HotkeySpec.ModControl | HotkeySpec.ModAlt)
    && parsed.KeyCode == 0x4D);
Check("解析大小写与空格不敏感", HotkeySpec.TryParse(" ctrl + shift + f5 ", out var messy)
    && messy.ToString() == "Ctrl+Shift+F5");
Check("解析 Win+Space", HotkeySpec.TryParse("Win+Space", out var winSpace)
    && winSpace.ToString() == "Win+Space");
Check("解析 Ctrl+Left", HotkeySpec.TryParse("Ctrl+Left", out var ctrlLeft)
    && ctrlLeft.ToString() == "Ctrl+Left");

Check("空串视为未设置", HotkeySpec.TryParse(string.Empty, out var empty) && empty.IsEmpty);
Check("null 视为未设置", HotkeySpec.TryParse(null, out var nullSpec) && nullSpec.IsEmpty);
Check("None 是未设置", HotkeySpec.None.IsEmpty);
Check("缺主键被拒", !HotkeySpec.TryParse("Ctrl+Alt", out _));
Check("缺修饰键被拒", !HotkeySpec.TryParse("M", out _));
Check("未知键名被拒", !HotkeySpec.TryParse("Ctrl+Alt+NoSuchKey", out _));
Check("功能键越界被拒", !HotkeySpec.TryParse("Ctrl+F25", out _));

// 9. 远端路径的 shell 转义
Check("转义含空格的路径", AdbService.ShellQuote("/sdcard/My Dir") == "'/sdcard/My Dir'");
Check("转义路径内的单引号", AdbService.ShellQuote("a'b") == "'a'\\''b'");
Check("转义中文路径", AdbService.ShellQuote("/sdcard/手机备份") == "'/sdcard/手机备份'");
Check(
    "push/pull 的两端路径用双引号",
    AdbService.WindowsQuote(@"C:\a b\c.txt") == "\"C:\\a b\\c.txt\""
        && AdbService.WindowsQuote("/sdcard/手机 备份") == "\"/sdcard/手机 备份\"");

// 10. `adb shell ls -la` 解析（toybox 格式）
var lsOutput = "total 8\n"
    + "drwxrwx--x  5 root   sdcard_rw  4096 2026-05-01 12:34 Android\n"
    + "-rw-rw----  1 u0_a123 media_rw 12345 2026-05-01 12:34 some file with spaces.txt\n"
    + "lrwxrwxrwx  1 root   root        21 2026-05-01 12:34 sdcard -> /storage/self/primary\n"
    + "-rw-rw----  1 u0_a123 media_rw   999 2026-05-01 12:34 .hidden\n"
    + "drwxrwx--x  2 root   sdcard_rw  4096 2026-05-01 12:34 .\n"
    + "drwxrwx--x  2 root   sdcard_rw  4096 2026-05-01 12:34 ..\n";

var entries = AdbService.ParseLsLong(lsOutput, "/sdcard");
Check("ls 解析出 4 条", entries.Count == 4, $"实际 {entries.Count}");
Check("total 行被跳过", entries.All(e => !e.Name.StartsWith("total", StringComparison.Ordinal)));
Check("目录标记正确", entries.First(e => e.Name == "Android").IsDirectory);
Check("文件名含空格完整保留", entries.Any(e => e.Name == "some file with spaces.txt"));
Check("大文件条目不是目录", !entries.First(e => e.Name == "some file with spaces.txt").IsDirectory);
Check("文件大小解析", entries.First(e => e.Name == "some file with spaces.txt").Size == 12345);
Check("修改时间解析", entries.First(e => e.Name == "Android").Modified == "2026-05-01 12:34");
Check("符号链接标记", entries.First(e => e.Name == "sdcard").IsLink);
Check("符号链接去掉箭头目标", !entries.Any(e => e.Name.Contains("->", StringComparison.Ordinal)));
Check("隐藏文件保留", entries.Any(e => e.Name == ".hidden"));
Check("点与点点被过滤", !entries.Any(e => e.Name is "." or ".."));
Check(
    "全路径拼接（父目录无斜杠）",
    entries.First(e => e.Name == "Android").FullPath == "/sdcard/Android");
Check(
    "全路径拼接（父目录带斜杠）",
    AdbService.ParseLsLong("drwxrwx--x 2 root root 4096 2026-05-01 12:34 sub\n", "/sdcard/")[0].FullPath
        == "/sdcard/sub");

// 11. 非 toybox 日期格式的退化解析
var legacy = AdbService.ParseLsLong(
    "drwxr-xr-x 2 root root 4096 May  1 12:34 Download\n"
    + "-rw-r--r-- 1 root root  100 May  1 12:34 a.txt\n",
    "/sdcard");
Check("退化格式仍能列出条目", legacy.Count == 2, $"实际 {legacy.Count}");
Check("退化格式目录标记正确", legacy.First(e => e.Name == "Download").IsDirectory);
Check("退化格式大小解析", legacy.First(e => e.Name == "a.txt").Size == 100);

Console.WriteLine(failures == 0 ? "\n全部通过" : $"\n失败 {failures} 项");
return failures == 0 ? 0 : 1;
