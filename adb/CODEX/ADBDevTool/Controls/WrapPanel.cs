/// @file    WrapPanel.cs
/// @author  Bowen
/// @module  ADBDevTool / 自动换行面板
///
/// 不变量：
/// 1. 必须在有限宽度的单元格里使用（星号列或固定宽度）；放进 Auto 列时 Measurement 拿到的是无限宽，永远不会换行。
/// 2. 子元素按自身 DesiredSize 排列，不做缩放；单元格比单个子元素还窄时才会越界。

using System;
using Windows.Foundation;
using Microsoft.UI.Xaml.Controls;

namespace ADBDevTool.Controls;

public sealed class WrapPanel : Panel
{
    public double HorizontalSpacing { get; set; } = 8;

    public double VerticalSpacing { get; set; } = 8;

    protected override Size MeasureOverride(Size availableSize)
    {
        var maxWidth = double.IsInfinity(availableSize.Width) ? double.PositiveInfinity : availableSize.Width;
        var lineWidth = 0.0;
        var lineHeight = 0.0;
        var totalWidth = 0.0;
        var totalHeight = 0.0;

        foreach (var child in Children)
        {
            child.Measure(new Size(maxWidth, double.PositiveInfinity));
            var desired = child.DesiredSize;

            if (lineWidth > 0 && lineWidth + HorizontalSpacing + desired.Width > maxWidth)
            {
                totalWidth = Math.Max(totalWidth, lineWidth);
                totalHeight += lineHeight + VerticalSpacing;
                lineWidth = 0;
                lineHeight = 0;
            }

            lineWidth += (lineWidth > 0 ? HorizontalSpacing : 0) + desired.Width;
            lineHeight = Math.Max(lineHeight, desired.Height);
        }

        // 返回的期望宽度必须收敛到可用宽度：否则父级 Grid 会被"不换行所需宽度"撑宽，可用宽度随之变大，
        // 换行判据永远不成立（实测表现为按钮排成一行并被窗口边缘裁掉）。
        return new Size(Math.Min(Math.Max(totalWidth, lineWidth), maxWidth), totalHeight + lineHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var x = 0.0;
        var y = 0.0;
        var lineHeight = 0.0;

        foreach (var child in Children)
        {
            var desired = child.DesiredSize;
            if (x > 0 && x + desired.Width > finalSize.Width)
            {
                x = 0;
                y += lineHeight + VerticalSpacing;
                lineHeight = 0;
            }

            var width = Math.Min(desired.Width, Math.Max(0, finalSize.Width - x));
            child.Arrange(new Rect(x, y, width, desired.Height));
            x += desired.Width + HorizontalSpacing;
            lineHeight = Math.Max(lineHeight, desired.Height);
        }

        return finalSize;
    }
}
