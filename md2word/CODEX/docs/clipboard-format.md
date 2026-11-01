# 剪贴板格式规范

本文规定「复制 Word」写入 `CF_HTML` 时使用的结构与公式格式。最后核对日期：2026-10-20。

## 官方依据

- [Microsoft 365 Math Documentation](https://learn.microsoft.com/en-us/office/math/)：Word、Excel、PowerPoint 和 OneNote 在文档中存储 OMML，并支持 MathML、LaTeX 等行业格式。
- [MathML Support in Microsoft 365](https://learn.microsoft.com/en-us/office/math/mathml)：Microsoft 365 应用（含 OneNote）支持导入和导出 MathML 3 Presentation MathML；Office 收到 MathML 后转换为 Office Math（OMML）。
- [LaTeX Support in Microsoft 365](https://learn.microsoft.com/en-us/office/math/latex)：LaTeX 导入范围主要面向 Word、Excel 和 PowerPoint，不能作为 OneNote 剪贴板公式的通用交换格式。
- [Application interface (OneNote)](https://learn.microsoft.com/en-us/office/client-developer/onenote/application-interface-onenote)：OneNote 页面内容通过 OneNote XML 读写。

## 剪贴板容器

1. 富文本使用 Windows `HTML Format`（`CF_HTML`），同时写入 `CF_UNICODETEXT` 纯文本兜底。
2. `StartHTML`、`EndHTML`、`StartFragment`、`EndFragment` 和 Selection 偏移均按 UTF-8 字节计算。
3. HTML 根节点只声明旧版 Word HTML 所需命名空间：

```html
<html xmlns:o="urn:schemas-microsoft-com:office:office"
      xmlns:w="urn:schemas-microsoft-com:office:word"
      xmlns="http://www.w3.org/TR/REC-html40">
```

4. 图片使用 base64 `data:` URI 内嵌，不依赖 `file://` 路径。
5. 纯文本兜底禁止输出 `<svg>`、MathML、OMML、警报块 class 或其它 HTML 标签。

## 公式格式

剪贴板公式使用 MathML 3 Presentation MathML，不使用 OMML：

```html
<math xmlns="http://www.w3.org/1998/Math/MathML"
      mathsize="14pt">
  <mi>γ</mi><mo>=</mo><mi>j</mi><mi>β</mi>
</math>
```

规则：

1. `<math>` 根元素必须声明 `http://www.w3.org/1998/Math/MathML`。本项目统一使用默认命名空间，不依赖前缀。
2. 行内公式不写 `display`；独立公式写 `display="block"`。
3. 字号写在 `<math>` 的 `mathsize` 属性上，单位使用 `pt`。
4. 标识符使用 `<mi>`，数字使用 `<mn>`，运算符与括号使用 `<mo>`，普通文本使用 `<mtext>`。
5. 粗体、斜体和普通体等样式使用 `mathvariant` 表达。
6. `mfrac` 必须正好包含 2 个内容节点，`munder` / `mover` 正好 2 个，`msub` / `msup` 正好 2 个，`msubsup` 正好 3 个。内容超过一个节点时必须用 `<mrow>` 包起来。
7. `lim`、`max`、`min` 等可移动上下限运算符使用 `<mo movablelimits="true" form="prefix">lim</mo>`，并与 `<munder>` / `<mover>` 配合。禁止生成节点数量不合法的 `<munder><mi>lim</mi><mi>τ</mi><mo>→</mo><mn>0</mn></munder>`。
8. 禁止在剪贴板 MathML 中自动添加 OMML 的 `m:nor`、`m:rPr` 或 WordprocessingML `w:rPr`。实践验证表明，OneNote 会把带 `m:nor` 的 run 拆成普通文本并插入 `&quot;`。
9. OMML 仅用于导出的 `.docx`。Office 在粘贴 MathML 时会自行转换为可编辑的 Office Math。
10. 不依赖 `<annotation>` 或 `<annotation-xml>` 保存渲染内容；官方文档说明这两个子元素当前会被忽略。
11. 不使用 `dir`；官方文档明确不支持该属性。
12. 字符优先使用字面 Unicode 或 XML 数字字符引用；HTML 命名实体仅限嵌入 HTML 时使用。

## 当前元素映射

剪贴板转换器支持以下 OMML 到 MathML 对照：

| OMML | MathML |
|---|---|
| `m:f` | `mfrac` |
| `m:rad` | `msqrt` / `mroot` |
| `m:sSub` / `m:sSup` / `m:sSubSup` | `msub` / `msup` / `msubsup` |
| `m:nary` | `munderover` / `munder` / `msubsup` / `msub` 等 |
| `m:d` | `mrow` + 显式 `mo` 定界符 |
| `m:acc` | `mover accent="true"` |
| `m:bar` / `m:groupChr` | `mover` / `munder` |
| `m:limLow` / `m:limUpp` | `munder` / `mover` |
| `m:func` | `mrow` + U+2061 函数应用符 |
| `m:m` / `m:eqArr` | `mtable` / `mtr` / `mtd` |
| `m:borderBox` | `menclose notation="box"` |
| `m:phant` | `mphantom` |

裸分隔符命令（如 `\langle`、`\rangle`、`\lceil`、`\rceil`、`\lfloor`、`\rfloor`、`\lvert`、`\rvert`）必须转换为对应的 Unicode `<mo>`；不得把反斜杠和命令名当作普通文本输出。

`cases`、`matrix`、`aligned` 等环境使用 `\\` 分列、`\\\\` 分行；LaTeX 的转义空格 `\ ` 必须规范化为普通空格，不得输出字面反斜杠。

LaTeX 字体声明 `\rm` 对当前分组内后续字符生效，离开分组后恢复默认数学样式；下标中的 `\rm zi` 必须生成普通体 `zi`，不得输出字面 `\rmzi`。

## 验收标准

1. Word 粘贴后，所有预期公式均存在于 `Document.OMaths`。
2. OneNote 粘贴后的页面 XML 中不得出现由公式转换产生的 `&quot;`。
3. 行内公式保持在同一文本段落，独立公式使用块级公式。
4. 公式字号与设置一致，MathML 中至少包含对应的 `mathsize`。
5. `lim` 的下限必须是单个 `<mrow>`，在 OneNote 中不得出现 `lim┬`、`linÿ` 或类似乱码。
6. `\langle ... \rangle` 必须生成 `⟨ ... ⟩`，不得残留字面 `\langle` 或 `\rangle`。
7. `cases` 必须生成包含两行单元格的 `mtable`；不得出现字面 `\0` 或把多行压成单行。
8. 按钮复制和后台快捷键必须通过同一个转换入口，并生成完全相同的 CF_HTML。
9. `\rm` 字体声明不得泄漏到分组外，也不得作为反斜杠文本输出。
10. 标准构建与单文件构建都必须通过同一组剪贴板回归测试。
