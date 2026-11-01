using MarkdownToWord.Cli;

namespace MarkdownToWord;

/// <summary>
/// 自建入口点：不带参数时启动 WinUI 界面，带 --convert 时走命令行转换。
/// </summary>
public static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        // 单文件发布时 Windows App SDK 的 DLL 会被解包到临时目录，必须先告诉它去哪找
        // （散文件发布下这个变量指向程序所在目录，同样正确）
        Environment.SetEnvironmentVariable(
            "MICROSOFT_WINDOWSAPPRUNTIME_BASE_DIRECTORY",
            AppContext.BaseDirectory);

        if (args.Length > 0 &&
            (args[0].Equals("--convert", StringComparison.OrdinalIgnoreCase) ||
             args[0].Equals("--math-probe", StringComparison.OrdinalIgnoreCase) ||
             args[0].Equals("--copy", StringComparison.OrdinalIgnoreCase)))
        {
            return CliRunner.Run(args);
        }

        // 中文 Windows 上的 .md 常见 GB18030 编码，需要注册代码页编码提供程序
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);

        WinRT.ComWrappersSupport.InitializeComWrappers();
        Microsoft.UI.Xaml.Application.Start(p =>
        {
            _ = p;
            var context = new Microsoft.UI.Dispatching.DispatcherQueueSynchronizationContext(
                Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            _ = new App();
        });

        return 0;
    }
}
