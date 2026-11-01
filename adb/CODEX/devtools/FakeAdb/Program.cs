/// @file    Program.cs
/// @author  Bowen
/// @module  ADBDevTool / 自检用假 adb
///
/// 契约：
/// 1. adb 侧记录取自 exe 同目录的 fake-state.txt：取值 "device"（已连接）、
///    "unauth"（在列表里但未授权）、"none"（列表为空）。文件缺失按 "none"。
/// 2. 手机侧是否接受握手取自 fake-phone.txt：值为 "refuse" 时 connect 回
///    "failed to authenticate to X" 并把记录写回 "unauth"，否则回 "connected to X" 并写 "device"。
///    文件缺失按接受。
/// 3. disconnect 把记录改写为 "none"，对应真机行为：摘掉残留 transport 后 connect 才会重新握手。
/// 4. 只实现 ADBDevTool 会调用的子命令；其余参数一律回 "ok" 并以退出码 0 结束。

using System;
using System.IO;
using System.Text;

var statePath = Path.Combine(AppContext.BaseDirectory, "fake-state.txt");
var state = File.Exists(statePath) ? File.ReadAllText(statePath).Trim() : "none";
var phonePath = Path.Combine(AppContext.BaseDirectory, "fake-phone.txt");
var phone = File.Exists(phonePath) ? File.ReadAllText(phonePath).Trim() : "allow";
var command = string.Join(' ', args);
var exitCode = 0;

Console.OutputEncoding = Encoding.UTF8;

switch (command)
{
    case "version":
        Console.Write("Android Debug Bridge version 1.0.41\nVersion 37.0.1-fake\n");
        break;

    case "start-server":
        Console.Write("* daemon started successfully\n");
        break;

    case "kill-server":
        File.WriteAllText(statePath, "none\n");
        break;

    case "devices":
    case "devices -l":
        Console.Write("List of devices attached\n");
        if (state == "device")
        {
            Console.Write("127.0.0.1:15037\tdevice product:PN74C model:24129PN74C device:dada transport_id:1\n");
        }
        else if (state is "unauth" or "unauthfix")
        {
            Console.Write("127.0.0.1:15037\tunauthorized transport_id:1\n");
        }

        break;

    case "mdns services":
        Console.Write("List of discovered mdns services\n");
        if (state == "probe")
        {
            // 一个可建连（由自检脚本在本机监听 15037）与一个必然不通的端口，用来验列表置灰。
            Console.Write("adb-probe-live\t_adb-tls-connect._tcp\t127.0.0.1:15037\n");
            Console.Write("adb-probe-dead\t_adb-tls-connect._tcp\t127.0.0.1:9\n");
        }
        else
        {
            Console.Write("adb-24129PN74C-1a2B3c\t_adb-tls-connect._tcp\t127.0.0.1:15037\n");
            Console.Write("adb-24129PN74C-1a2B3c\t_adb-tls-pairing._tcp\t127.0.0.1:37001\n");
        }

        break;

    default:
        if (command.StartsWith("disconnect ", StringComparison.Ordinal))
        {
            File.WriteAllText(statePath, "none\n");
            Console.Write($"disconnected {command["disconnect ".Length..]}\n");
        }
        else if (command.StartsWith("connect ", StringComparison.Ordinal))
        {
            var endpoint = command["connect ".Length..].Trim();
            if (phone == "refuse")
            {
                File.WriteAllText(statePath, "unauth\n");
                Console.Write($"failed to authenticate to {endpoint}\n");
                exitCode = 1;
            }
            else if (state == "device")
            {
                Console.Write($"already connected to {endpoint}\n");
            }
            else
            {
                File.WriteAllText(statePath, "device\n");
                Console.Write($"connected to {endpoint}\n");
            }
        }
        else
        {
            Console.Write("ok\n");
        }

        break;
}

return exitCode;
