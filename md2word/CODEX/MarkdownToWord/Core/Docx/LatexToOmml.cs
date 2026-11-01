using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Math;

namespace MarkdownToWord.Core.Docx;

/// <summary>
/// LaTeX 数学公式 → Word 原生公式（OMML）。
///
/// 设计要点：
/// 1. 只覆盖理工科文档常见的 LaTeX 子集，解析失败时退化为"原样显示 LaTeX 源码"，绝不丢内容。
/// 2. 属性名（m:chr、m:type、m:pos…）统一用 SetAttribute 写入，
///    避免依赖 SDK 里容易记错的具体属性类。
/// </summary>
internal static class LatexToOmml
{
    // ------------------------------------------------------------ 符号表

    private static readonly Dictionary<string, string> Symbols = new(StringComparer.Ordinal)
    {
        // 小写希腊字母
        ["alpha"] = "\u03B1", ["beta"] = "\u03B2", ["gamma"] = "\u03B3", ["delta"] = "\u03B4",
        ["epsilon"] = "\u03B5", ["varepsilon"] = "\u03F5", ["zeta"] = "\u03B6", ["eta"] = "\u03B7",
        ["theta"] = "\u03B8", ["vartheta"] = "\u03D1", ["iota"] = "\u03B9", ["kappa"] = "\u03BA",
        ["lambda"] = "\u03BB", ["mu"] = "\u03BC", ["nu"] = "\u03BD", ["xi"] = "\u03BE",
        ["omicron"] = "\u03BF", ["pi"] = "\u03C0", ["varpi"] = "\u03D6", ["rho"] = "\u03C1",
        ["varrho"] = "\u03F1", ["sigma"] = "\u03C3", ["varsigma"] = "\u03C2", ["tau"] = "\u03C4",
        ["upsilon"] = "\u03C5", ["phi"] = "\u03C6", ["varphi"] = "\u03D5", ["chi"] = "\u03C7",
        ["psi"] = "\u03C8", ["omega"] = "\u03C9",
        // 大写希腊字母
        ["Gamma"] = "\u0393", ["Delta"] = "\u0394", ["Theta"] = "\u0398", ["Lambda"] = "\u039B",
        ["Xi"] = "\u039E", ["Pi"] = "\u03A0", ["Sigma"] = "\u03A3", ["Upsilon"] = "\u03A5",
        ["Phi"] = "\u03A6", ["Psi"] = "\u03A8", ["Omega"] = "\u03A9",
        // 运算与关系
        ["times"] = "\u00D7", ["div"] = "\u00F7", ["pm"] = "\u00B1", ["mp"] = "\u2213",
        ["cdot"] = "\u22C5", ["ast"] = "\u2217", ["star"] = "\u22C6", ["circ"] = "\u2218",
        ["bullet"] = "\u2219", ["oplus"] = "\u2295", ["ominus"] = "\u2296", ["otimes"] = "\u2297",
        ["odot"] = "\u2299", ["le"] = "\u2264", ["leq"] = "\u2264", ["ge"] = "\u2265",
        ["geq"] = "\u2265", ["ne"] = "\u2260", ["neq"] = "\u2260", ["equiv"] = "\u2261",
        ["approx"] = "\u2248", ["cong"] = "\u2245", ["simeq"] = "\u2243", ["sim"] = "\u223C",
        ["propto"] = "\u221D", ["doteq"] = "\u2250", ["ll"] = "\u226A", ["gg"] = "\u226B",
        ["prec"] = "\u227A", ["succ"] = "\u227B", ["preceq"] = "\u2AAF", ["succeq"] = "\u2AB0",
        // 集合与逻辑
        ["in"] = "\u2208", ["notin"] = "\u2209", ["ni"] = "\u220B", ["subset"] = "\u2282",
        ["subseteq"] = "\u2286", ["supset"] = "\u2283", ["supseteq"] = "\u2287",
        ["cup"] = "\u222A", ["cap"] = "\u2229", ["setminus"] = "\u2216", ["emptyset"] = "\u2205",
        ["varnothing"] = "\u2205", ["forall"] = "\u2200", ["exists"] = "\u2203",
        ["nexists"] = "\u2204", ["neg"] = "\u00AC", ["lnot"] = "\u00AC", ["land"] = "\u2227",
        ["wedge"] = "\u2227", ["lor"] = "\u2228", ["vee"] = "\u2228", ["therefore"] = "\u2234",
        ["because"] = "\u2235", ["models"] = "\u22A8", ["vdash"] = "\u22A2",
        // 箭头
        ["to"] = "\u2192", ["rightarrow"] = "\u2192", ["leftarrow"] = "\u2190",
        ["gets"] = "\u2190", ["leftrightarrow"] = "\u2194", ["Rightarrow"] = "\u21D2",
        ["Leftarrow"] = "\u21D0", ["Leftrightarrow"] = "\u21D4", ["mapsto"] = "\u21A6",
        ["uparrow"] = "\u2191", ["downarrow"] = "\u2193", ["updownarrow"] = "\u2195",
        ["Uparrow"] = "\u21D1", ["Downarrow"] = "\u21D3", ["Updownarrow"] = "\u21D5",
        ["nearrow"] = "\u2197", ["searrow"] = "\u2198", ["swarrow"] = "\u2199", ["nwarrow"] = "\u2196",
        ["longrightarrow"] = "\u27F6", ["longleftarrow"] = "\u27F5",
        ["longleftrightarrow"] = "\u27F7", ["Longrightarrow"] = "\u27F9",
        ["Longleftarrow"] = "\u27F8", ["Longleftrightarrow"] = "\u27FA",
        ["implies"] = "\u27F9", ["impliedby"] = "\u27F8", ["iff"] = "\u27FA",
        ["longmapsto"] = "\u27FC", ["hookrightarrow"] = "\u21AA", ["hookleftarrow"] = "\u21A9",
        ["twoheadrightarrow"] = "\u21A0", ["twoheadleftarrow"] = "\u219E",
        ["rightharpoonup"] = "\u21C0", ["rightharpoondown"] = "\u21C1",
        ["leftharpoonup"] = "\u21BC", ["leftharpoondown"] = "\u21BD",
        ["rightleftharpoons"] = "\u21CC", ["leftrightharpoons"] = "\u21CB",
        // 可独立使用的定界符命令；无 \left/\right 时仍必须转换为数学符号。
        ["langle"] = "\u27E8", ["rangle"] = "\u27E9",
        ["lceil"] = "\u2308", ["rceil"] = "\u2309",
        ["lfloor"] = "\u230A", ["rfloor"] = "\u230B",
        ["vert"] = "|", ["Vert"] = "\u2016",
        ["lvert"] = "|", ["rvert"] = "|",
        ["lVert"] = "\u2016", ["rVert"] = "\u2016",
        ["lbrace"] = "{", ["rbrace"] = "}",
        ["lbrack"] = "[", ["rbrack"] = "]",
        // 大型运算符
        ["sum"] = "\u2211", ["prod"] = "\u220F", ["coprod"] = "\u2210", ["int"] = "\u222B",
        ["iint"] = "\u222C", ["iiint"] = "\u222D", ["oint"] = "\u222E",
        ["bigcup"] = "\u22C3", ["bigcap"] = "\u22C2", ["bigoplus"] = "\u2A01",
        ["bigotimes"] = "\u2A02", ["bigodot"] = "\u2A00", ["bigvee"] = "\u22C1",
        ["bigwedge"] = "\u22C0", ["bigsqcup"] = "\u2A06",
        // 其它符号
        ["infty"] = "\u221E", ["partial"] = "\u2202", ["nabla"] = "\u2207", ["Re"] = "\u211C",
        ["Im"] = "\u2111", ["aleph"] = "\u2135", ["hbar"] = "\u210F", ["ell"] = "\u2113",
        ["wp"] = "\u2118", ["prime"] = "\u2032", ["angle"] = "\u2220", ["perp"] = "\u22A5",
        ["parallel"] = "\u2225", ["triangle"] = "\u25B3", ["square"] = "\u25A1",
        ["diamond"] = "\u25C7", ["surd"] = "\u221A", ["checkmark"] = "\u2713",
        ["cdots"] = "\u22EF", ["ldots"] = "\u2026", ["dots"] = "\u2026", ["vdots"] = "\u22EE",
        ["ddots"] = "\u22F1", ["degree"] = "\u00B0", ["S"] = "\u00A7", ["P"] = "\u00B6",
        ["copyright"] = "\u00A9", ["pounds"] = "\u00A3",
        // 转义字符
        ["{"] = "{", ["}"] = "}", ["%"] = "%", ["&"] = "&", ["#"] = "#", ["$"] = "$",
        ["_"] = "_", ["|"] = "\u2016", ["backslash"] = "\\", [" "] = " ",
    };

    private static readonly HashSet<string> BigOperators = new(StringComparer.Ordinal)
    {
        "sum", "prod", "coprod", "bigcup", "bigcap", "bigoplus", "bigotimes", "bigodot",
        "bigvee", "bigwedge", "bigsqcup",
    };

    private static readonly HashSet<string> NaryOperators = new(StringComparer.Ordinal)
    {
        "int", "iint", "iiint", "oint",
    };

    private static readonly HashSet<string> LimitOperators = new(StringComparer.Ordinal)
    {
        "lim", "limsup", "liminf", "max", "min", "sup", "inf", "det", "dim", "ker", "deg",
        "gcd", "hom", "arg", "Pr", "injlim", "projlim",
    };

    private static readonly HashSet<string> UprightFunctions = new(StringComparer.Ordinal)
    {
        "sin", "cos", "tan", "cot", "sec", "csc", "arcsin", "arccos", "arctan",
        "sinh", "cosh", "tanh", "coth", "log", "ln", "lg", "exp", "mod", "bmod", "pmod",
    };

    private static readonly Dictionary<string, string> AccentChars = new(StringComparer.Ordinal)
    {
        ["hat"] = "\u0302",
        ["widehat"] = "\u0302",
        ["tilde"] = "\u0303",
        ["widetilde"] = "\u0303",
        ["bar"] = "\u0304",
        ["vec"] = "\u20D7",
        ["dot"] = "\u0307",
        ["ddot"] = "\u0308",
        ["acute"] = "\u0301",
        ["grave"] = "\u0300",
        ["check"] = "\u030C",
        ["breve"] = "\u0306",
        ["ring"] = "\u030A",
    };

    private static readonly Dictionary<string, string> Delimiters = new(StringComparer.Ordinal)
    {
        ["("] = "(", [")"] = ")", ["["] = "[", ["]"] = "]", ["{"] = "{", ["}"] = "}",
        ["|"] = "|", ["."] = string.Empty, ["\\"] = "\\", ["langle"] = "\u27E8",
        ["rangle"] = "\u27E9", ["lceil"] = "\u2308", ["rceil"] = "\u2309",
        ["lfloor"] = "\u230A", ["rfloor"] = "\u230B", ["vert"] = "|", ["Vert"] = "\u2016",
        ["lvert"] = "|", ["rvert"] = "|", ["lVert"] = "\u2016", ["rVert"] = "\u2016",
    };

    private static readonly Dictionary<char, string> Blackboard = new()
    {
        ['A'] = "\U0001D538", ['B'] = "\U0001D539", ['C'] = "\u2102", ['D'] = "\U0001D53B",
        ['E'] = "\U0001D53C", ['F'] = "\U0001D53D", ['G'] = "\U0001D53E", ['H'] = "\u210D",
        ['I'] = "\U0001D540", ['J'] = "\U0001D541", ['K'] = "\U0001D542", ['L'] = "\U0001D543",
        ['M'] = "\U0001D544", ['N'] = "\u2115", ['O'] = "\U0001D546", ['P'] = "\u2119",
        ['Q'] = "\u211A", ['R'] = "\u211D", ['S'] = "\U0001D54A", ['T'] = "\U0001D54B",
        ['U'] = "\U0001D54C", ['V'] = "\U0001D54D", ['W'] = "\U0001D54E", ['X'] = "\U0001D54F",
        ['Y'] = "\U0001D550", ['Z'] = "\u2124",
    };

    private static readonly Dictionary<char, string> Calligraphic = new()
    {
        ['A'] = "\U0001D49C", ['B'] = "\u212C", ['C'] = "\U0001D49E", ['D'] = "\U0001D49F",
        ['E'] = "\u2130", ['F'] = "\u2131", ['G'] = "\U0001D4A2", ['H'] = "\u210B",
        ['I'] = "\u2110", ['J'] = "\U0001D4A5", ['K'] = "\U0001D4A6", ['L'] = "\u2112",
        ['M'] = "\u2133", ['N'] = "\U0001D4A9", ['O'] = "\U0001D4AA", ['P'] = "\U0001D4AB",
        ['Q'] = "\U0001D4AC", ['R'] = "\u211B", ['S'] = "\U0001D4AE", ['T'] = "\U0001D4AF",
        ['U'] = "\U0001D4B0", ['V'] = "\U0001D4B1", ['W'] = "\U0001D4B2", ['X'] = "\U0001D4B3",
        ['Y'] = "\U0001D4B4", ['Z'] = "\U0001D4B5",
    };

    // ------------------------------------------------------------ 对外接口

    /// <summary>把 LaTeX 公式转成 m:oMath（行内公式用）。</summary>
    public static OfficeMath ToOfficeMath(string latex)
    {
        var math = new OfficeMath();
        try
        {
            var parser = new Parser(latex ?? string.Empty);
            var nodes = parser.ParseRow(stopAt: null);
            foreach (var node in nodes)
            {
                math.AppendChild(node);
            }
        }
        catch
        {
            math.RemoveAllChildren();
            math.AppendChild(MathRun(latex ?? string.Empty, upright: false));
        }

        if (!math.HasChildren)
        {
            math.AppendChild(MathRun(string.Empty, upright: false));
        }

        return math;
    }

    /// <summary>把 LaTeX 公式转成 m:oMathPara（独立成行的公式用）。</summary>
    public static Paragraph ToMathParagraph(string latex)
    {
        return new Paragraph(ToOfficeMath(latex));
    }

    // ------------------------------------------------------------ OMML 构造辅助

    internal static Run MathRun(string text, bool upright, bool bold = false, bool italic = true)
    {
        var run = new Run();
        var props = new RunProperties();

        if (upright)
        {
            props.AppendChild(new NormalText());
        }
        else if (bold && italic)
        {
            props.AppendChild(new Style { Val = StyleValues.BoldItalic });
        }
        else if (bold)
        {
            props.AppendChild(new Style { Val = StyleValues.Bold });
        }
        else if (!italic)
        {
            props.AppendChild(new Style { Val = StyleValues.Plain });
        }

        if (props.HasChildren)
        {
            run.AppendChild(props);
        }

        run.AppendChild(new Text(text));
        return run;
    }

    private static Base MathBase(IEnumerable<OpenXmlElement> children)
    {
        var list = children as IList<OpenXmlElement> ?? children.ToList();
        if (list.Count == 1)
        {
            return MathBase(list[0]);
        }

        var b = new Base();
        foreach (var child in list)
        {
            b.AppendChild(child);
        }

        if (!b.HasChildren)
        {
            b.AppendChild(MathRun(string.Empty, upright: false));
        }

        return b;
    }

    /// <summary>把单个元素装进参数容器；如果已经是 m:e 就原样返回，避免 m:e 套 m:e。</summary>
    private static Base MathBase(OpenXmlElement child)
    {
        return child is Base existing ? existing : new Base(child);
    }

    private static Fraction MakeFraction(
        IEnumerable<OpenXmlElement> numerator,
        IEnumerable<OpenXmlElement> denominator,
        bool noBar = false)
    {
        var fraction = new Fraction();
        if (noBar)
        {
            fraction.AppendChild(new FractionProperties(
                new FractionType { Val = FractionTypeValues.NoBar }));
        }

        // m:num / m:den 本身就是参数容器，内容直接是公式元素
        fraction.AppendChild(new Numerator(numerator.ToArray()));
        fraction.AppendChild(new Denominator(denominator.ToArray()));
        return fraction;
    }

    private static OpenXmlElement MakeDelimiterFromNodes(
        IEnumerable<OpenXmlElement> content,
        string beginChar,
        string endChar,
        string? separator = null)
    {
        var props = new DelimiterProperties();
        // 必须显式写出空定界符（m:val=""），否则 Word 会套用默认的圆括号
        props.AppendChild(new BeginChar { Val = beginChar });
        props.AppendChild(new EndChar { Val = endChar });

        if (!string.IsNullOrEmpty(separator))
        {
            props.AppendChild(new SeparatorChar { Val = separator });
        }

        // 整个括号内容放在同一个 m:e 里：m:d 的多个 m:e 之间会被 Word 插入分隔符（默认 |）
        var delimiter = new Delimiter(props);
        delimiter.AppendChild(MathBase(content));
        return delimiter;
    }

    private static OpenXmlElement MakeNary(
        string chr,
        IEnumerable<OpenXmlElement>? sub,
        IEnumerable<OpenXmlElement>? sup,
        IEnumerable<OpenXmlElement>? body,
        bool underOver)
    {
        var props = new NaryProperties();
        props.AppendChild(new AccentChar { Val = chr });
        props.AppendChild(new LimitLocation
        {
            Val = underOver ? LimitLocationValues.UnderOver : LimitLocationValues.SubscriptSuperscript,
        });

        if (sub is null)
        {
            props.AppendChild(new HideSubArgument { Val = BooleanValues.True });
        }

        if (sup is null)
        {
            props.AppendChild(new HideSuperArgument { Val = BooleanValues.True });
        }

        var nary = new Nary(props);
        // OMML 要求 nary 同时带 m:sub 与 m:sup 节点；缺上限/下限时用空节点占位并靠
        // supHide/subHide 隐藏。缺节点时 Word 可解析，OneNote 的 HTML 粘贴会丢弃整个段落。
        nary.AppendChild(new SubArgument(sub?.ToArray() ?? []));
        nary.AppendChild(new SuperArgument(sup?.ToArray() ?? []));

        nary.AppendChild(MathBase(body ?? [MathRun(string.Empty, upright: false)]));
        return nary;
    }

    private static OpenXmlElement MakeAccent(string accentChar, OpenXmlElement body)
    {
        var props = new AccentProperties(new AccentChar { Val = accentChar });
        return new Accent(props, MathBase(body));
    }

    private static OpenXmlElement MakeBar(bool top, OpenXmlElement body)
    {
        var props = new BarProperties(new Position
        {
            Val = top ? VerticalJustificationValues.Top : VerticalJustificationValues.Bottom,
        });

        return new Bar(props, MathBase(body));
    }

    private static OpenXmlElement MakeLowerLimit(OpenXmlElement body, IEnumerable<OpenXmlElement> limit)
    {
        var limitLower = new LimitLower();
        limitLower.AppendChild(MathBase(body));
        limitLower.AppendChild(new Limit(limit.ToArray()));
        return limitLower;
    }

    // ------------------------------------------------------------ 解析器

    private sealed class Parser(string source)
    {
        private readonly string _s = source;
        private int _i;
        private bool _uprightMode;

        private bool Eof => _i >= _s.Length;

        private char Peek => _i < _s.Length ? _s[_i] : '\0';

        private char PeekAt(int offset) => _i + offset < _s.Length ? _s[_i + offset] : '\0';

        /// <summary>解析一串公式，直到遇到 stopAt 里的字符（不消费该字符）。</summary>
        public List<OpenXmlElement> ParseRow(string? stopAt, bool stopAtRightDelimiter = false)
        {
            var nodes = new List<OpenXmlElement>();
            while (!Eof)
            {
                if (stopAtRightDelimiter && IsAt("\\right"))
                {
                    break;
                }

                var c = Peek;
                if (stopAt is not null && stopAt.IndexOf(c) >= 0)
                {
                    break;
                }

                if (c == '}')
                {
                    break;
                }

                if (char.IsWhiteSpace(c))
                {
                    // LaTeX 数学态里空格不产生符号，但 \text 之外的连续空格也没意义。
                    _i++;
                    continue;
                }

                // 行分隔与列分隔（在矩阵/方程组里由上层消费，走到这里说明是散落的，直接过掉）
                if (c == '&' || (c == '\\' && PeekAt(1) == '\\'))
                {
                    break;
                }

                var atom = ParseAtomWithScripts();
                if (atom is not null)
                {
                    nodes.AddRange(atom);
                }
            }

            return nodes;
        }

        private List<OpenXmlElement>? ParseAtomWithScripts()
        {
            var atom = ParseAtom();
            if (atom is null)
            {
                return null;
            }

            List<OpenXmlElement>? sub = null;
            List<OpenXmlElement>? sup = null;

            while (!Eof)
            {
                var c = Peek;
                if (c == '^' && PeekAt(1) != '^')
                {
                    _i++;
                    sup ??= ParseScriptArgument();
                    continue;
                }

                if (c == '_')
                {
                    _i++;
                    sub ??= ParseScriptArgument();
                    continue;
                }

                if (c == '\'' && atom.Count == 1)
                {
                    _i++;
                    var primes = new List<OpenXmlElement> { MathRun("\u2032", upright: false) };
                    while (Peek == '\'')
                    {
                        _i++;
                        primes.Add(MathRun("\u2032", upright: false));
                    }

                    sup = primes;
                    continue;
                }

                break;
            }

            if (sub is null && sup is null)
            {
                return atom;
            }

            var baseElement = atom.Count == 1 ? atom[0] : MathBase(atom);
            var result = new List<OpenXmlElement>();

            if (sub is not null && sup is not null)
            {
                result.Add(new SubSuperscript(
                    MathBase(baseElement),
                    new SubArgument(sub.ToArray()),
                    new SuperArgument(sup.ToArray())));
            }
            else if (sup is not null)
            {
                result.Add(new Superscript(
                    MathBase(baseElement),
                    new SuperArgument(sup.ToArray())));
            }
            else
            {
                result.Add(new Subscript(
                    MathBase(baseElement),
                    new SubArgument(sub!.ToArray())));
            }

            return result;
        }

        private List<OpenXmlElement> ParseScriptArgument()
        {
            SkipSpaces();
            if (Peek == '{')
            {
                _i++;
                var uprightMode = _uprightMode;
                var nodes = ParseRow(stopAt: null);
                _uprightMode = uprightMode;
                if (Peek == '}')
                {
                    _i++;
                }

                return nodes.Count > 0 ? nodes : [MathRun(string.Empty, upright: false)];
            }

            var single = ParseAtom();
            return single is { Count: > 0 } ? single : [MathRun(string.Empty, upright: false)];
        }

        private List<OpenXmlElement>? ParseAtom()
        {
            SkipSpaces();
            if (Eof)
            {
                return null;
            }

            var c = Peek;

            if (c == '{')
            {
                _i++;
                var uprightMode = _uprightMode;
                var nodes = ParseRow(stopAt: null);
                _uprightMode = uprightMode;
                if (Peek == '}')
                {
                    _i++;
                }

                return nodes;
            }

            if (c == '}')
            {
                _i++;
                return [MathRun("}", upright: false)];
            }

            if (c == '\\')
            {
                return ParseCommand();
            }

            _i++;
            return [MathRun(c.ToString(), upright: _uprightMode)];
        }

        private void SkipSpaces()
        {
            while (!Eof && char.IsWhiteSpace(Peek))
            {
                _i++;
            }
        }

        private string ReadCommandName()
        {
            // 已经消费了反斜杠
            var sb = new StringBuilder();
            if (!Eof && !char.IsLetter(Peek))
            {
                sb.Append(Peek);
                _i++;
                return sb.ToString();
            }

            while (!Eof && char.IsLetter(Peek))
            {
                sb.Append(Peek);
                _i++;
            }

            return sb.ToString();
        }

        private List<OpenXmlElement> ParseCommand()
        {
            _i++; // 吃掉反斜杠
            var name = ReadCommandName();

            switch (name)
            {
                case "frac":
                case "dfrac":
                case "tfrac":
                {
                    var num = ParseRequiredGroup();
                    var den = ParseRequiredGroup();
                    return [MakeFraction(num, den)];
                }

                case "binom":
                case "dbinom":
                case "tbinom":
                {
                    var top = ParseRequiredGroup();
                    var bottom = ParseRequiredGroup();
                    var fraction = MakeFraction(top, bottom, noBar: true);
                    return [MakeDelimiterFromNodes([fraction], "(", ")")];
                }

                case "sqrt":
                {
                    List<OpenXmlElement>? degree = null;
                    SkipSpaces();
                    if (Peek == '[')
                    {
                        _i++;
                        degree = ParseRow(stopAt: "]");
                        if (Peek == ']')
                        {
                            _i++;
                        }
                    }

                    var body = ParseRequiredGroup();
                    var radical = new Radical();
                    if (degree is not null)
                    {
                        radical.AppendChild(new Degree(degree.ToArray()));
                    }
                    else
                    {
                        // CT_Rad 里 m:deg 是必选元素：没有次数时写一个空 deg + degHide
                        radical.AppendChild(new RadicalProperties(
                            new HideDegree { Val = BooleanValues.True }));
                        radical.AppendChild(new Degree());
                    }

                    radical.AppendChild(MathBase(body));
                    return [radical];
                }

                case "text":
                case "textrm":
                case "textnormal":
                {
                    var content = ReadRawGroupText();
                    return [MathRun(content, upright: true)];
                }

                case "mathrm":
                case "operatorname":
                {
                    var nodes = ParseRequiredGroup();
                    return ApplyUpright(nodes);
                }

                case "mathbf":
                case "boldsymbol":
                case "bm":
                {
                    var nodes = ParseRequiredGroup();
                    return MarkBold(nodes);
                }

                case "mathit":
                {
                    var nodes = ParseRequiredGroup();
                    return MarkItalic(nodes);
                }

                case "mathcal":
                case "mathscr":
                {
                    var nodes = ParseRequiredGroup();
                    return MapCharacters(nodes, Calligraphic);
                }

                case "mathbb":
                {
                    var nodes = ParseRequiredGroup();
                    return MapCharacters(nodes, Blackboard);
                }

                case "mathsf":
                case "mathtt":
                case "mathfrak":
                {
                    var nodes = ParseRequiredGroup();
                    return ApplyUpright(nodes);
                }

                case "overline":
                {
                    var nodes = ParseRequiredGroup();
                    return [MakeBar(true, MathBase(nodes))];
                }

                case "underline":
                {
                    var nodes = ParseRequiredGroup();
                    return [MakeBar(false, MathBase(nodes))];
                }

                case "overbrace":
                case "underbrace":
                {
                    var nodes = ParseRequiredGroup();
                    var props = new GroupCharProperties(
                        new AccentChar { Val = name == "overbrace" ? "\u23DE" : "\u23DF" },
                        new Position
                        {
                            Val = name == "overbrace"
                                ? VerticalJustificationValues.Top
                                : VerticalJustificationValues.Bottom,
                        });

                    var group = new GroupChar(props);
                    group.AppendChild(MathBase(nodes));
                    return [group];
                }

                case "boxed":
                {
                    var nodes = ParseRequiredGroup();
                    return [new BorderBox(MathBase(nodes))];
                }

                case "phantom":
                case "hphantom":
                case "vphantom":
                {
                    var nodes = ParseRequiredGroup();
                    return [new Phantom(MathBase(nodes))];
                }

                case "left":
                    return ParseDelimited();

                case "right":
                {
                    // 落单的 \right，读掉它的定界符即可
                    SkipSpaces();
                    if (!Eof)
                    {
                        _i++;
                    }

                    return [];
                }

                case "begin":
                    return ParseEnvironment();

                case "limits":
                case "nolimits":
                case "displaystyle":
                case "textstyle":
                case "scriptstyle":
                case "scriptscriptstyle":
                case "!":
                case ",":
                case ":":
                case ";":
                case ">":
                case " ":
                    return [];

                case "rm":
                    _uprightMode = true;
                    return [];

                case "quad":
                    return [MathRun("\u2003", upright: true)];

                case "qquad":
                    return [MathRun("\u2003\u2003", upright: true)];

                case "\\":
                {
                    // 行分隔，由上层处理；这里直接结束
                    return [];
                }

                default:
                    return ParseCommandFallback(name);
            }
        }

        /// <summary>
        /// \xrightarrow{above} / \xleftarrow{above}，以及笔记简写 \xrightarrowddt（箭头上方标 d/dt）。
        /// ReadCommandName 连续读取字母，简写会把紧跟的字母一起读入（如 \xrightarrowddtu）；
        /// 多读的部分按 _i 回退，交回 ParseRow 继续解析。
        /// </summary>
        private bool TryParseArrowCommand(string name, out List<OpenXmlElement> nodes)
        {
            nodes = [];
            string? arrow = null;
            var hasInlineLabel = false;

            if (name == "xrightarrow")
            {
                arrow = "\u2192";
            }
            else if (name == "xleftarrow")
            {
                arrow = "\u2190";
            }
            else if (name.StartsWith("xrightarrowddt", StringComparison.Ordinal))
            {
                arrow = "\u2192";
                hasInlineLabel = true;
                _i -= name.Length - "xrightarrowddt".Length;
            }

            if (arrow is null)
            {
                return false;
            }

            List<OpenXmlElement>? above = null;
            if (hasInlineLabel)
            {
                above =
                [
                    MakeFraction(
                        [MathRun("d", upright: true)],
                        [MathRun("d", upright: true), MathRun("t", upright: true)]),
                ];
            }
            else
            {
                SkipSpaces();
                if (Peek == '{')
                {
                    above = ParseRequiredGroup();
                }
            }

            var arrowRun = MathRun(arrow, upright: false);
            nodes = above is null
                ? [arrowRun]
                : [new LimitUpper(MathBase(arrowRun), new Limit(above.ToArray()))];
            return true;
        }

        private List<OpenXmlElement> ParseCommandFallback(string name)
        {
            if (TryParseArrowCommand(name, out var arrowNodes))
            {
                return arrowNodes;
            }

            if (AccentChars.TryGetValue(name, out var accent))
            {
                var nodes = ParseRequiredGroup();
                return [MakeAccent(accent, MathBase(nodes))];
            }

            if (LimitOperators.Contains(name))
            {
                var body = MathRun(name, upright: true);
                List<OpenXmlElement>? limit = null;
                SkipSpaces();
                if (Peek == '_')
                {
                    _i++;
                    limit = ParseScriptArgument();
                }

                if (limit is not null)
                {
                    return [MakeLowerLimit(body, limit)];
                }

                return [body];
            }

            if (BigOperators.Contains(name) || NaryOperators.Contains(name))
            {
                var symbol = Symbols.TryGetValue(name, out var ch) ? ch : name;
                List<OpenXmlElement>? sub = null;
                List<OpenXmlElement>? sup = null;

                while (true)
                {
                    SkipSpaces();
                    if (Peek == '_' && sub is null)
                    {
                        _i++;
                        sub = ParseScriptArgument();
                        continue;
                    }

                    if (Peek == '^' && sup is null)
                    {
                        _i++;
                        sup = ParseScriptArgument();
                        continue;
                    }

                    break;
                }

                var following = ParseOptionalOperand();
                var underOver = BigOperators.Contains(name) || sub is not null || sup is not null;

                // 积分类默认上下限在右侧
                if (NaryOperators.Contains(name) && sub is null && sup is null)
                {
                    underOver = false;
                }

                return [MakeNary(symbol, sub, sup, following, underOver)];
            }

            if (UprightFunctions.Contains(name))
            {
                return [MathRun(name, upright: true)];
            }

            if (Symbols.TryGetValue(name, out var symbolChar))
            {
                return [MathRun(symbolChar, upright: false)];
            }

            // 未知命令：原样保留命令名，避免内容丢失
            return [MathRun("\\" + name, upright: true)];
        }

        /// <summary>把 \sum 后面的操作数收进 nary 的基底（LaTeX 的语义就是这么写的）。</summary>
        private List<OpenXmlElement>? ParseOptionalOperand()
        {
            SkipSpaces();
            if (Eof || Peek is '}' or '&' or ']' or ')')
            {
                return null;
            }

            if (Peek == '\\' && PeekAt(1) == '\\')
            {
                return null;
            }

            var atom = ParseAtomWithScripts();
            return atom is { Count: > 0 } ? atom : null;
        }

        private List<OpenXmlElement> ParseDelimited()
        {
            SkipSpaces();
            var begin = ReadDelimiterToken();
            var content = new List<OpenXmlElement>();

            while (!Eof)
            {
                // 注意：不能用 "吃掉 \\r 再读命令名" 的写法，那样永远匹配不到 \right
                if (IsAt("\\right"))
                {
                    _i += "\\right".Length;
                    SkipSpaces();
                    var end = ReadDelimiterToken();
                    return [MakeDelimiterFromNodes(content, begin, end)];
                }

                var before = _i;
                var nodes = ParseRow(stopAt: null, stopAtRightDelimiter: true);
                content.AddRange(nodes);
                if (_i == before)
                {
                    break;
                }
            }

            return [MakeDelimiterFromNodes(content, begin, ")")];
        }

        private string ReadDelimiterToken()
        {
            if (Eof)
            {
                return string.Empty;
            }

            if (Peek == '\\')
            {
                _i++;
                var name = ReadCommandName();
                return Delimiters.TryGetValue(name, out var mapped) ? mapped : name;
            }

            var c = Peek;
            _i++;
            return Delimiters.TryGetValue(c.ToString(), out var literal) ? literal : c.ToString();
        }

        private List<OpenXmlElement> ParseEnvironment()
        {
            var envName = ReadRawGroupText().Trim();
            SkipSpaces();

            // array/tabular 的列格式说明 {ccc} 直接丢掉
            string? columnSpec = null;
            if (envName is "array" or "tabular" or "matrix*")
            {
                SkipSpaces();
                if (Peek == '{')
                {
                    columnSpec = ReadRawGroupText();
                }
            }

            _ = columnSpec;

            var body = ReadUntilEnvironmentEnd(envName);
            var rows = SplitRows(body);

            switch (envName)
            {
                case "matrix":
                case "pmatrix":
                case "bmatrix":
                case "Bmatrix":
                case "vmatrix":
                case "Vmatrix":
                case "smallmatrix":
                {
                    var matrix = new Matrix();
                    foreach (var row in rows)
                    {
                        var matrixRow = new MatrixRow();
                        foreach (var cell in SplitColumns(row))
                        {
                            var parser = new Parser(cell);
                            matrixRow.AppendChild(MathBase(parser.ParseRow(stopAt: null)));
                        }

                        if (!matrixRow.HasChildren)
                        {
                            matrixRow.AppendChild(MathBase(MathRun(string.Empty, upright: false)));
                        }

                        matrix.AppendChild(matrixRow);
                    }

                    var (open, close) = envName switch
                    {
                        "pmatrix" => ("(", ")"),
                        "bmatrix" => ("[", "]"),
                        "Bmatrix" => ("{", "}"),
                        "vmatrix" => ("|", "|"),
                        "Vmatrix" => ("\u2016", "\u2016"),
                        _ => (string.Empty, string.Empty),
                    };

                    return open.Length == 0
                        ? [matrix]
                        : [MakeDelimiterFromNodes([matrix], open, close)];
                }

                case "cases":
                case "dcases":
                {
                    var array = BuildEquationArray(rows, joinColumns: true);
                    return [MakeDelimiterFromNodes([array], "{", string.Empty)];
                }

                case "aligned":
                case "align":
                case "align*":
                case "gathered":
                case "gather":
                case "gather*":
                case "split":
                case "array":
                case "tabular":
                case "subarray":
                default:
                {
                    var array = BuildEquationArray(rows, joinColumns: true);
                    return [array];
                }
            }
        }

        /// <summary>构造 m:eqArr；joinColumns=true 时把同一行的多列合并成一段文本。</summary>
        private static EquationArray BuildEquationArray(List<string> rows, bool joinColumns)
        {
            var array = new EquationArray();
            foreach (var row in rows)
            {
                var content = joinColumns ? string.Join("  ", SplitColumns(row)) : row;
                var parser = new Parser(content);
                array.AppendChild(MathBase(parser.ParseRow(stopAt: null)));
            }

            if (!array.HasChildren)
            {
                array.AppendChild(MathBase(MathRun(string.Empty, upright: false)));
            }

            return array;
        }

        private static List<string> SplitRows(string body)
        {
            var rows = new List<string>();
            var depth = 0;
            var sb = new StringBuilder();

            for (var i = 0; i < body.Length; i++)
            {
                var c = body[i];
                if (c == '\\' && i + 1 < body.Length)
                {
                    if (body[i + 1] == '\\' && depth == 0)
                    {
                        rows.Add(sb.ToString());
                        sb.Clear();
                        i++;
                        continue;
                    }

                    sb.Append(c).Append(body[i + 1]);
                    i++;
                    continue;
                }

                if (c == '{')
                {
                    depth++;
                }
                else if (c == '}')
                {
                    depth = Math.Max(0, depth - 1);
                }

                sb.Append(c);
            }

            if (sb.Length > 0)
            {
                rows.Add(sb.ToString());
            }

            return rows;
        }

        private static List<string> SplitColumns(string row)
        {
            var cells = new List<string>();
            var depth = 0;
            var sb = new StringBuilder();

            for (var i = 0; i < row.Length; i++)
            {
                var c = row[i];
                if (c == '\\' && i + 1 < row.Length)
                {
                    sb.Append(c).Append(row[i + 1]);
                    i++;
                    continue;
                }

                if (c == '{')
                {
                    depth++;
                }
                else if (c == '}')
                {
                    depth = Math.Max(0, depth - 1);
                }
                else if (c == '&' && depth == 0)
                {
                    cells.Add(sb.ToString());
                    sb.Clear();
                    continue;
                }

                sb.Append(c);
            }

            cells.Add(sb.ToString());
            return cells;
        }

        private string ReadUntilEnvironmentEnd(string envName)
        {
            var sb = new StringBuilder();
            var depth = 0;
            var needle = "\\end{" + envName + "}";

            while (!Eof)
            {
                if (Peek == '\\' && PeekAt(1) == 'b' && IsAt("\\begin{"))
                {
                    depth++;
                    sb.Append("\\begin{");
                    _i += "\\begin{".Length;
                    continue;
                }

                if (depth == 0 && IsAt(needle))
                {
                    _i += needle.Length;
                    break;
                }

                if (IsAt("\\end{"))
                {
                    depth = Math.Max(0, depth - 1);
                    sb.Append("\\end{");
                    _i += "\\end{".Length;
                    continue;
                }

                sb.Append(_s[_i]);
                _i++;
            }

            return sb.ToString();
        }

        private bool IsAt(string token)
        {
            if (_i + token.Length > _s.Length)
            {
                return false;
            }

            return string.CompareOrdinal(_s, _i, token, 0, token.Length) == 0;
        }

        private List<OpenXmlElement> ParseRequiredGroup()
        {
            SkipSpaces();
            if (Peek == '{')
            {
                _i++;
                var uprightMode = _uprightMode;
                var nodes = ParseRow(stopAt: null);
                _uprightMode = uprightMode;
                if (Peek == '}')
                {
                    _i++;
                }

                return nodes;
            }

            var atom = ParseAtomWithScripts();
            return atom ?? [MathRun(string.Empty, upright: false)];
        }

        /// <summary>读取 {…} 的原始文本（不做数学解析），用于 \text、\operatorname 与环境名。</summary>
        private string ReadRawGroupText()
        {
            SkipSpaces();
            if (Peek != '{')
            {
                if (Eof)
                {
                    return string.Empty;
                }

                var c = Peek;
                _i++;
                return c.ToString();
            }

            _i++;
            var depth = 1;
            var sb = new StringBuilder();
            while (!Eof)
            {
                var c = _s[_i];
                if (c == '\\' && _i + 1 < _s.Length)
                {
                    var next = _s[_i + 1];
                    if (next is '{' or '}' or '\\')
                    {
                        sb.Append(next);
                        _i += 2;
                        continue;
                    }

                    sb.Append(c);
                    _i++;
                    continue;
                }

                if (c == '{')
                {
                    depth++;
                }
                else if (c == '}')
                {
                    depth--;
                    if (depth == 0)
                    {
                        _i++;
                        break;
                    }
                }

                sb.Append(c);
                _i++;
            }

            return sb.ToString();
        }

        // -------------------------------------------------------- 样式映射

        private static List<OpenXmlElement> ApplyUpright(List<OpenXmlElement> nodes)
        {
            foreach (var run in EnumerateRuns(nodes))
            {
                var props = run.GetFirstChild<RunProperties>();
                if (props is null)
                {
                    props = new RunProperties();
                    run.InsertAt(props, 0);
                }

                if (!props.Elements<NormalText>().Any())
                {
                    props.AppendChild(new NormalText());
                }
            }

            return nodes;
        }

        private static List<OpenXmlElement> MarkBold(List<OpenXmlElement> nodes)
        {
            foreach (var run in EnumerateRuns(nodes))
            {
                var props = run.GetFirstChild<RunProperties>();
                if (props is null)
                {
                    props = new RunProperties();
                    run.InsertAt(props, 0);
                }

                ReplaceStyle(props, StyleValues.Bold);
            }

            return nodes;
        }

        private static List<OpenXmlElement> MarkItalic(List<OpenXmlElement> nodes)
        {
            foreach (var run in EnumerateRuns(nodes))
            {
                var props = run.GetFirstChild<RunProperties>();
                if (props is null)
                {
                    props = new RunProperties();
                    run.InsertAt(props, 0);
                }

                ReplaceStyle(props, StyleValues.Italic);
            }

            return nodes;
        }

        private static List<OpenXmlElement> MapCharacters(List<OpenXmlElement> nodes, Dictionary<char, string> map)
        {
            foreach (var text in nodes.SelectMany(EnumerateTexts))
            {
                var sb = new StringBuilder();
                foreach (var ch in text.Text ?? string.Empty)
                {
                    sb.Append(map.TryGetValue(ch, out var mapped) ? mapped : ch.ToString());
                }

                text.Text = sb.ToString();
            }

            return ApplyUpright(nodes);
        }

        private static IEnumerable<Run> EnumerateRuns(IEnumerable<OpenXmlElement> nodes)
        {
            foreach (var node in nodes)
            {
                foreach (var run in EnumerateRuns(node))
                {
                    yield return run;
                }
            }
        }

        private static IEnumerable<Run> EnumerateRuns(OpenXmlElement node)
        {
            foreach (var descendant in node.Descendants<Run>())
            {
                yield return descendant;
            }
        }

        private static IEnumerable<Text> EnumerateTexts(OpenXmlElement node)
        {
            foreach (var text in node.Descendants<Text>())
            {
                yield return text;
            }
        }

        /// <summary>替换 m:rPr 里的 m:sty（同一时刻只允许一个样式元素）。</summary>
        private static void ReplaceStyle(RunProperties props, StyleValues style)
        {
            foreach (var existing in props.Elements<Style>().ToList())
            {
                existing.Remove();
            }

            props.AppendChild(new Style { Val = style });
        }
    }
}
