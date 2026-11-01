/// @file    SplitterThumb.cs
/// @author  Bowen
/// @module  ADBDevTool / 可拖动分隔条
///
/// 说明：
/// 1. UIElement.ProtectedCursor 是 protected 成员，要换鼠标指针形状必须派生一个类型。
/// 2. WinUI 的 Border 是 sealed，无法继承，因此以 ContentControl 为基类，圆角背景放在内容里。

using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Controls;

namespace ADBDevTool.Controls;

public sealed class SplitterThumb : ContentControl
{
    /// <summary>把鼠标指针换成拖动形状（东西向用于列分隔，南北向用于行分隔）。</summary>
    public void UseCursor(InputSystemCursorShape shape) => ProtectedCursor = InputSystemCursor.Create(shape);
}
