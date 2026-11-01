# Markdown 转 Word · 离线版（WinUI 3）

`markdowntoword.io/zh` 核心功能（Markdown → Word）的**完全离线**桌面复刻版，用 WinUI 3 + Windows App SDK 1.8 实现，
转换全部在本机完成，不联网、不上传、不依赖任何在线服务。

## 目录结构

| 路径 | 说明 |
| --- | --- |
| `dist/MarkdownToWord/` | **可直接运行的程序**：双击 `MarkdownToWord.exe` |
| `dist/MarkdownToWord-SingleFile/` | **单文件版**：就一个 `MarkdownToWord.exe`（218 MB，运行时全在里面） |
| `MarkdownToWord/` | 完整源码工程（.NET 10 + WinUI 3） |
| `MarkdownToWord/Samples/syntax-tour.md` | 语法覆盖测试样例（本文档的大量验证都基于它） |
| `docs/clipboard-format.md` | 剪贴板 HTML、图片和 MathML 格式规范及 Microsoft 官方依据 |
| `_tools/` | 本次开发用的辅助脚本：API 反射、文档校验、Word 转 PDF、界面截图、程序图标生成（`make-app-icon.ps1`）、剪贴板格式检查等 |
| `_output/` | 验证产物：生成的 docx / pdf / 页面截图 / 校验输出 |
| `BACKUP/DEL/` | 按你的规则，所有被替换掉的旧产物都移动到这里，没有删除任何文件 |

## 怎么用

1. 双击 `dist/MarkdownToWord/MarkdownToWord.exe`（免安装、免注册表，Windows App SDK 已随程序自带）。
   点击窗口关闭按钮不会退出程序，而是隐藏到系统托盘；双击托盘图标恢复窗口，右键托盘图标选择“退出”才会结束进程。
2. 三种输入方式任选：左侧直接编辑/粘贴、`打开` 选择 `.md` 文件、把 `.md` 文件拖进窗口。
3. 右侧实时预览（代码高亮 + 公式渲染），点 `导出 Word` 选择保存位置即可得到 `.docx`。
4. 不想存文件时点 `复制 Word`：剪贴板里是 HTML 富文本，在 Word 里 Ctrl+V 会把正文带格式插到光标处，沿用目标文档的页面设置；公式写的是 MathML，Office 粘贴时转换为 **原生 Office 公式**；标题、列表、表格、代码高亮、图片都保留。
   在编辑框里**粘贴**后会自动按最新内容重跑一次同样的复制（设置 → 剪贴板 →「粘贴后自动复制」，默认开）。
   在右侧预览里单击后按 `Ctrl+C`（没有选中文字时）也会复制当前 Markdown 的 Word 富文本，而不是复制预览里的示例图片。

   复制成功的横幅 1 秒后自动收起（警告和错误会留着等人处理）。
5. `设置` 里可调页面尺寸、方向、页边距、正文字体字号，以及标题编号 / 代码高亮 / 公式 / 网络图片等开关，设置会自动记住。
   「窗口快捷键」有独立启用开关，默认开启，按键默认是 `Alt+D`：程序不在前台时恢复并聚焦，已在前台时最小化。点击快捷键按钮后按下新组合键，再按回车确认；按 `Esc` 取消录入。
   「后台剪贴板转换」默认关闭且没有快捷键。开启后可录入第二组组合键；按下时读取剪贴板纯文本，在后台替换为 Word 富文本，只发送 Windows 系统通知，不切换前台窗口。

快捷键：全局 `Alt+D` 显示/最小化窗口（可在设置中关闭或修改）、可配置的后台剪贴板转换（默认关闭且无按键）、`Ctrl+O` 打开、`Ctrl+S` 保存 Markdown、`Ctrl+E` 导出 Word。

## 两个发行版：散文件版 / 单文件版

| 版本 | 路径 | 说明 |
| --- | --- | --- |
| 散文件版 | `dist\MarkdownToWord\` | 688 个文件、约 178 MB。启动最快（不用解压），直接双击 `MarkdownToWord.exe`。 |
| 单文件版 | `dist\MarkdownToWord-SingleFile\MarkdownToWord.exe` | **只有一个 exe**（218 MB），.NET 运行时 + Windows App SDK + KaTeX/highlight.js 全部打包在内，拷到任何 Win10 1809+/Win11 上双击就能跑。 |

单文件版的几个要点：

- 重新生成：`_tools\publish-singlefile.ps1`（等价于 `dotnet publish -c Release -r win-x64 -p:SingleFilePublish=true`）。
  这个开关会同时打开 `EnableMsixTooling` / `IncludeAllContentForSelfExtract` 等 Windows App SDK 要求的属性，见工程里的同名 PropertyGroup。
- **首次启动**会把自己解包到 `%TEMP%\.net\MarkdownToWord\<hash>\`（约 1~2 秒），之后复用同一份，所以只有第一次略慢。
- 唯一的外部依赖是 **WebView2 运行时**（Edge 自带，Win10 21H2+/Win11 默认就有）。没有它时只影响右侧实时预览，导出 Word / 复制正文照常工作。
- 体积为什么这么大：`SelfContained` 把 .NET 运行时（约 70 MB）和 Windows App SDK 自包含运行时都装了进去，其中还含本程序用不到的 AI/ML 组件（`onnxruntime.dll` 20.7 MB、`DirectML.dll` 17.8 MB）。后续若想瘦身，可以把 `Microsoft.WindowsAppSDK` 换成只引 `Microsoft.WindowsAppSDK.WinUI` 等子包。

### 命令行模式（批量转换）

```
MarkdownToWord.exe --convert <input.md> <output.docx> [选项]
选项：
  --no-math            不解析 LaTeX 公式（按原文输出）
  --no-highlight       关闭代码高亮
  --no-remote          禁止下载远程图片
  --number-headings    标题自动编号
  --page=A4|Letter|A5|Legal
  --margin=2.54        页边距（厘米）
  --validate           用 OpenXML SDK 校验产物是否符合 OOXML 架构
```

另有 `--copy <input.md> [--copy-as=text|doc] [--hold=秒]`：把内容放进剪贴板（等价于界面上的「复制 Word」，不弹界面）。

- `--copy-as=text`（默认）：放 HTML 富文本（带格式的正文 + Word 原生公式）。这是静态剪贴板数据，进程退出照样有效。
- `--copy-as=doc`：放 .docx 文件。剪贴板用的 docx 落在 `%LOCALAPPDATA%\MarkdownToWord\Clipboard\`，
  必须留在磁盘上，粘贴时才有内容；内容由本进程按需渲染（与资源管理器一致），所以**进程退出后粘贴就取不到内容**，
  命令行里可以加 `--hold=30` 把进程顶住若干秒。

## 语法覆盖

已实现并在样例中逐项验证：

| 类别 | 覆盖内容 |
| --- | --- |
| 块级 | H1–H6（映射 Word 内置标题样式，可用导航窗格）、段落、分隔线、引用（含嵌套）、提示块 `> [!NOTE]` |
| 列表 | 无序（多层不同符号）、有序（支持自定义起始编号，如从 5 开始）、任务列表（☑ / ☐）、定义列表 |
| 表格 | 管道表格、网格表格、列对齐（左/中/右）、表头底纹、斑马纹、跨列合并 |
| 行内 | 粗体、斜体、粗斜体、删除线 `~~`、高亮 `==`、下划线 `++`、上标 `^`、行内代码、转义字符、HTML 实体；下标用 LaTeX（`\(x_1\)`），单个 `~` 按普通字符输出（`0.85~15 MHz`、`104~106` 不被当成下标定界符） |
| 代码 | 围栏代码块与缩进代码块，25+ 语言的轻量语法高亮（关键字/字符串/注释/数字/函数分色） |
| 公式 | `$…$`、`$$…$$`，以及 `\(…\)`、`\[…\]`（自动归一化）；转为 **Word 原生公式（OMML）**，可继续编辑 |

复制到 Word 时，公式内部字符会使用设置中的正文字号；数学变量仍按 Word 的 Cambria Math 数学样式显示。这样做可避免 OneNote 把带 `m:nor` 的行内公式拆成带引号的普通文本，分数、根式、上下标等 OMML 结构保持不变。
| 链接与媒体 | 普通链接、自动链接、邮箱、行内 HTML `<img>`、本地/`file://`/`data:`/http(s) 图片，自动按页面宽度缩放 |
| 结构化 | 脚注（真正的 Word 脚注）、YAML front matter（写入文档属性并可生成标题）、缩写、Emoji、SmartyPants 智能标点 |
| 其它 | CJK 友好的强调解析（中文加粗不再串行）、HTML 块降级为文本、未知语法降级显示而不是丢弃 |

## 实现要点

- **解析**：Markdig 1.4（开启管道/网格表格、脚注、定义列表、任务列表、EmphasisExtras、数学、警报块、CJK 友好强调等扩展）。
- **导出**：直接用 Open XML SDK 3.5 生成 `docx`（自建 `styles.xml` / `numbering.xml` / `footnotes.xml`），不依赖 Word，也不需要 Office。
- **公式**：自写 LaTeX → OMML 转换器，支持分式、根式、上下标、求和/积分上下限、`\xrightarrow{…}` / `\xleftarrow{…}`（含笔记简写 `\xrightarrowddt`）、`\left…\right` 定界符、矩阵、`cases` 分段函数、希腊字母、长短箭头与常用符号表。
- **预览**：WebView2 + 本地 KaTeX / highlight.js（共 1.4 MB，已随程序打包），通过虚拟主机映射加载，完全离线。
- **预览宿主**：主窗口被 DWM cloak（切换虚拟桌面）或最小化期间折叠 `WebView2`，恢复时重新显示。WebView2 的宿主顶层窗口（`Chrome_WidgetWin_1`）不随 cloak 隐藏，留在当前桌面时仍接受文件拖放（WebView2 运行时 1.0.x）。
- **全局快捷键**：Win32 `RegisterHotKey` 注册到本进程的 message-only 窗口；`WM_HOTKEY` 只在主窗口无权切到前台时复用 `Activate` + `SetForegroundWindow`。
- **界面**：WinUI 3（非打包 / 自带运行时），中英文字体分别设置，浅色/深色/跟随系统主题。
- **系统托盘**：Win32 `Shell_NotifyIcon` 承载托盘图标和右键菜单；隐藏顶层消息窗口负责双击与右键回调，关闭主窗口时取消关闭并调用 `AppWindow.Hide()`。
- **剪贴板**：走 shell 的 `IDataObject`（`SHBindToParent` + `IShellFolder.GetUIObjectOf`）+ `OleSetClipboard`，和资源管理器复制文件完全一致。刻意**不用** WinRT 的 `DataPackage.SetStorageItems`：它只写出带 `FD_LINKUI` 的 `FileGroupDescriptorW`，既没有配套的 `FileContents` 数据流、`FileNameW` 还是空串，Word 会把它当成"链接到某个文件的对象"，取内容失败即弹"Word 无法获取 … 链接的数据"。同样刻意**不调用** `OleFlushClipboard`：实测 Flush 会把 `FileContents` 丢掉，等于把老毛病搬回来。
- **带格式的正文（HTML 剪贴板）**：Markdown 用同一套 Markdig 管线渲染成 HTML。公式按 Microsoft 365 官方规范输出 MathML 3 Presentation MathML，Office 在粘贴时转换为可编辑的 Office Math；OMML 只用于生成 `.docx`。图片以 base64 `data:` URI 内嵌，最后包成 CF_HTML 通过 Win32 静态剪贴板写入（附一份清理过 HTML/SVG 标记的纯文本兜底）。完整规则与官方链接见 [`docs/clipboard-format.md`](docs/clipboard-format.md)。

## 验证记录

1. **架构校验**：对 4 页语法样例产物运行 `OpenXmlValidator`，**0 错误**（校验项覆盖样式、编号、表格、脚注、OMML 公式）。
2. **渲染校验**：用本机 Word 把产物导出 PDF，再逐页转 PNG 人工核对——标题、列表、表格、引用、代码高亮、图片、脚注、公式排版均正确。
3. **运行校验**：Debug 与 Release 发布版都实际启动截图确认界面、实时预览、公式与代码高亮正常。

配套脚本：`_tools/run-cli-test.ps1`（转换+校验）、`_tools/docx-to-pdf.ps1`（Word 导出 PDF）、`_tools/inspect-docx.ps1`（拆包看 XML）、`_tools/capture-window.ps1`（窗口截图）。

## 已知限制

- **Mermaid 流程图**不做渲染：离线渲染需要额外引擎，` ```mermaid ` 代码块会原样保留为代码块（内容不丢）。
- **SVG 图片**不嵌入（Word 对 SVG 的支持依赖版本），会降级为占位文本提示。
- **远程图片**默认允许下载，断网时自动降级为占位文本；可在设置里彻底关闭。
- 预览的代码高亮语言集与导出时一致，但预览使用 highlight.js、导出使用内置着色器，配色不保证逐像素相同。
- **剪贴板生命周期**：HTML 和 Unicode 纯文本都是静态剪贴板数据，程序退出后仍可粘贴；纯文本兜底不再输出 SVG、`markdown-alert` 等 HTML 标记。
- **「复制 Word」的保真度**：走 Word 的 HTML 粘贴，脚注会退化成正文尾部引用而不是真正的 Word 脚注、「标题自动编号」不生效，缩写 `title` 提示在 Word 中也会丢失。要 100% 保真（真脚注、编号、页面设置）请用 `导出 Word`。
