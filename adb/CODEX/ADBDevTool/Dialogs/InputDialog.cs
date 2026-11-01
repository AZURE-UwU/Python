/// @file    InputDialog.cs
/// @author  Bowen
/// @module  ADBDevTool / 文本输入对话框

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ADBDevTool.Dialogs;

internal static class InputDialog
{
    /// <summary>弹出若干单行输入框；用户确认时返回与 fields 顺序一致的文本，取消返回 null。</summary>
    public static async Task<string[]?> ShowAsync(
        XamlRoot xamlRoot,
        string title,
        string message,
        string primaryButtonText,
        params (string Label, string Value)[] fields)
    {
        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap });

        var boxes = new List<TextBox>();
        foreach (var (label, value) in fields)
        {
            panel.Children.Add(new TextBlock { Text = label });
            var box = new TextBox { Text = value, Width = 320 };
            panel.Children.Add(box);
            boxes.Add(box);
        }

        var dialog = new ContentDialog
        {
            XamlRoot = xamlRoot,
            Title = title,
            Content = panel,
            PrimaryButtonText = primaryButtonText,
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
        };

        var result = await dialog.ShowAsync();
        return result == ContentDialogResult.Primary
            ? boxes.Select(box => box.Text).ToArray()
            : null;
    }
}
