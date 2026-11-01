using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Wordprocessing;

namespace MarkdownToWord.Core.Docx;

/// <summary>
/// 项目符号 / 编号定义。每遇到一个列表就分配一个新的 numId，
/// 这样嵌套列表与"从 5 开始"这类自定义起点都能各自独立。
/// </summary>
internal sealed class NumberingBuilder
{
    private const int LevelCount = 5;
    private static readonly string[] BulletChars = ["\u2022", "\u25CB", "\u25AA", "\u2022", "\u25CB"];
    private static readonly NumberFormatValues[] OrderedFormats =
    [
        NumberFormatValues.Decimal,
        NumberFormatValues.LowerLetter,
        NumberFormatValues.LowerRoman,
        NumberFormatValues.Decimal,
        NumberFormatValues.LowerLetter,
    ];

    private readonly Numbering _root = new()
    { };

    private readonly int _bulletAbstractId;
    private readonly int _orderedAbstractId;
    private readonly int _plainAbstractId;
    private int _nextNumId = 1;

    public NumberingBuilder()
    {
        _bulletAbstractId = AddAbstractNumbering(BuildBulletAbstract());
        _orderedAbstractId = AddAbstractNumbering(BuildOrderedAbstract());
        _plainAbstractId = AddAbstractNumbering(BuildPlainAbstract());
    }

    /// <summary>新建一个无序列表编号实例。</summary>
    public int CreateBulletList() => CreateInstance(_bulletAbstractId, null);

    /// <summary>新建一个有序列表编号实例。</summary>
    public int CreateOrderedList(int? startNumber) => CreateInstance(_orderedAbstractId, startNumber);

    /// <summary>新建一个"无符号"编号实例，用于任务列表（保留缩进、隐藏符号）。</summary>
    public int CreatePlainList() => CreateInstance(_plainAbstractId, null);

    public Numbering Build() => _root;

    private int AddAbstractNumbering(AbstractNum abstractNum)
    {
        var id = _root.Elements<AbstractNum>().Count();
        abstractNum.AbstractNumberId = id;
        _root.AppendChild(abstractNum);
        return id;
    }

    private int CreateInstance(int abstractId, int? startNumber)
    {
        var numId = _nextNumId++;
        var instance = new NumberingInstance(new AbstractNumId { Val = abstractId })
        {
            NumberID = numId,
        };

        if (startNumber is > 1)
        {
            instance.AppendChild(new LevelOverride(new StartOverrideNumberingValue { Val = startNumber })
            {
                LevelIndex = 0,
            });
        }

        _root.AppendChild(instance);
        return numId;
    }

    private static AbstractNum BuildBulletAbstract()
    {
        var abstractNum = CreateAbstractNumShell();
        for (var level = 0; level < LevelCount; level++)
        {
            abstractNum.AppendChild(new Level(
                new StartNumberingValue { Val = 1 },
                new NumberingFormat { Val = NumberFormatValues.Bullet },
                new LevelText { Val = BulletChars[level] },
                new LevelJustification { Val = LevelJustificationValues.Left },
                new PreviousParagraphProperties(
                    new Indentation
                    {
                        Left = (720 + (360 * level)).ToString(),
                        Hanging = "360",
                    }),
                new NumberingSymbolRunProperties(
                    new RunFonts { Ascii = "Arial", HighAnsi = "Arial", Hint = FontTypeHintValues.Default }))
            {
                LevelIndex = level,
            });
        }

        return abstractNum;
    }

    private static AbstractNum BuildOrderedAbstract()
    {
        var abstractNum = CreateAbstractNumShell();
        for (var level = 0; level < LevelCount; level++)
        {
            abstractNum.AppendChild(new Level(
                new StartNumberingValue { Val = 1 },
                new NumberingFormat { Val = OrderedFormats[level] },
                new LevelText { Val = $"%{level + 1}." },
                new LevelJustification { Val = LevelJustificationValues.Left },
                new PreviousParagraphProperties(
                    new Indentation
                    {
                        Left = (720 + (360 * level)).ToString(),
                        Hanging = "360",
                    }))
            {
                LevelIndex = level,
            });
        }

        return abstractNum;
    }

    private static AbstractNum BuildPlainAbstract()
    {
        var abstractNum = CreateAbstractNumShell();
        for (var level = 0; level < LevelCount; level++)
        {
            abstractNum.AppendChild(new Level(
                new StartNumberingValue { Val = 1 },
                new NumberingFormat { Val = NumberFormatValues.None },
                new LevelText { Val = string.Empty },
                new LevelJustification { Val = LevelJustificationValues.Left },
                new PreviousParagraphProperties(
                    new Indentation
                    {
                        Left = (720 + (360 * level)).ToString(),
                        Hanging = "360",
                    }))
            {
                LevelIndex = level,
            });
        }

        return abstractNum;
    }

    private static AbstractNum CreateAbstractNumShell()
    {
        return new AbstractNum(
            new MultiLevelType { Val = MultiLevelValues.HybridMultilevel });
    }
}
