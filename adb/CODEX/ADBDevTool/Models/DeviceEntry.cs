/// @file    DeviceEntry.cs
/// @author  Bowen
/// @module  ADBDevTool / 设备列表项

using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace ADBDevTool.Models;

// x:Bind 的 x:DataType 需在 XAML 编译器里可解析，故类型为 public。
public sealed class DeviceEntry
{
    public required string Endpoint { get; init; }

    public required string DisplayName { get; init; }

    public required string Detail { get; init; }

    public required string StateText { get; init; }

    public required Color BadgeColor { get; init; }

    /// <summary>该条目对应的端口可以直接用于 adb connect。</summary>
    public required bool CanConnect { get; init; }

    /// <summary>该条目对应的是 mdns 配对端口，需要先输入配对码。</summary>
    public required bool IsPairingService { get; init; }

    /// <summary>可作为 adb disconnect 参数的 serial；为空表示该设备不支持断开（USB 连接即如此）。</summary>
    public IReadOnlyList<string> DisconnectTargets { get; init; } = Array.Empty<string>();

    public bool CanDisconnect => DisconnectTargets.Count > 0;

    /// <summary>列表排序权重：已连接 0、可连接 1、待配对 2、待授权 3、其它 4。</summary>
    public required int SortKey { get; init; }

    /// <summary>端口探活失败（端口连不上）的条目：整行压暗、禁止连接，但仍留在列表里。</summary>
    public bool IsStale { get; init; }

    /// <summary>行不透明度：失效条目压暗到 0.55。</summary>
    public double RowOpacity => IsStale ? 0.55 : 1.0;

    public SolidColorBrush BadgeBrush => new(BadgeColor);
}
