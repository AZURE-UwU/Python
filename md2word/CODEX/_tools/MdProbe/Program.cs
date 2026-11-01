using System.Text;
using Markdig;
using Markdig.Extensions.Mathematics;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

var pipeline = new MarkdownPipelineBuilder()
    .UsePipeTables()
    .UseGridTables()
    .UseDefinitionLists()
    .UseFootnotes()
    .UseYamlFrontMatter()
    .UseTaskLists()
    .UseListExtras()
    .UseEmphasisExtras()
    .UseAutoLinks()
    .UseEmojiAndSmiley()
    .UseAbbreviations()
    .UseCjkFriendlyEmphasis()
    .UseNonAsciiNoEscape()
    .UseAutoIdentifiers()
    .UseCustomContainers()
    .UseAlertBlocks()
    .UseSmartyPants()
    .UsePreciseSourceLocation()
    .UseMathematics()
    .Build();

var samples = new (string Name, string Text)[]
{
    ("A 简单 $$ 块", "$$\nx = 1\n$$\n"),
    ("B 含矩阵的 $$ 块", "$$\n\\begin{pmatrix} a & b \\\\ c & d \\end{pmatrix}\n$$\n"),
    ("C 行内 \\(...\\)", "行内 \\(a \\ne 0\\) 结束。\n"),
    ("D 行内 $...$", "行内 $a \\ne 0$ 结束。\n"),
    ("E 多行公式块", "$$\nA = \\begin{pmatrix} a_{11} & a_{12} \\\\ a_{21} & a_{22} \\end{pmatrix},\n\\quad\nf(x) = \\begin{cases} x^2, & x \\ge 0 \\\\ -x, & x < 0 \\end{cases}\n$$\n"),
    ("F 任务列表", "- [x] 已完成\n- [ ] 待办\n"),
    ("G 无空行的 $$ 块", "$$\nx = 1\n$$\n\n文本\n"),
};

foreach (var (name, text) in samples)
{
    Console.WriteLine("================ " + name);
    var doc = Markdown.Parse(text, pipeline);
    Dump(doc, 0);
}

Console.WriteLine("================ HTML 输出样例");
Console.WriteLine(Markdown.ToHtml(
    "行内 $a \\ne 0$ 结束。\n\n$$\n\\sum_{i=1}^{n} i\n$$\n\n```csharp\nvar x = 1;\n```\n",
    pipeline));

return;

void Dump(MarkdownObject node, int depth)
{
    var indent = new string(' ', depth * 2);
    var name = node.GetType().Name;
    var extra = new StringBuilder();

    switch (node)
    {
        case MathBlock mb:
            extra.Append($"  <-- m:mathblock Info='{mb.Info}'");
            break;
        case FencedCodeBlock fcb:
            extra.Append($"  Info='{fcb.Info}'");
            break;
        case LiteralInline li:
            extra.Append($"  Text='{Escape(li.Content.ToString())}'");
            break;
        case MathInline mi:
            extra.Append($"  Content='{Escape(mi.Content.ToString())}' Delim='{mi.Delimiter}'");
            break;
        case ParagraphBlock pb when pb.Inline is not null:
            break;
    }

    Console.WriteLine($"{indent}{name}{extra}");

    switch (node)
    {
        case ContainerBlock cb:
            foreach (var child in cb)
            {
                Dump(child, depth + 1);
            }

            break;
        case LeafBlock lb when lb.Inline is not null:
            DumpInline(lb.Inline, depth + 1);
            break;
    }
}

void DumpInline(ContainerInline container, int depth)
{
    for (var child = container.FirstChild; child is not null; child = child.NextSibling)
    {
        Dump(child, depth);
    }
}

static string Escape(string value) => value.Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t");
