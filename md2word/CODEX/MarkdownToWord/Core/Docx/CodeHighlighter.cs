namespace MarkdownToWord.Core.Docx;

/// <summary>一段着色后的代码文本。</summary>
internal readonly record struct CodeToken(string Text, string? Color, bool Bold = false, bool Italic = false);

/// <summary>
/// 轻量代码着色器。目标不是 100% 准确的语法分析，
/// 而是让导出的 Word 代码块具备"关键字/字符串/注释/数字"四类可读的分色。
/// </summary>
internal static class CodeHighlighter
{
    private const string ColorKeyword = "D73A49";
    private const string ColorType = "005CC5";
    private const string ColorString = "032F62";
    private const string ColorComment = "6A737D";
    private const string ColorNumber = "005CC5";
    private const string ColorFunction = "6F42C1";
    private const string ColorConstant = "005CC5";
    private const string ColorPunct = "24292F";
    private const string ColorAdd = "22863A";
    private const string ColorDel = "B31D28";

    private sealed class Profile
    {
        public HashSet<string> Keywords { get; init; } = new(StringComparer.Ordinal);

        public HashSet<string> Types { get; init; } = new(StringComparer.Ordinal);

        public HashSet<string> Constants { get; init; } = new(StringComparer.Ordinal);

        public string[] LineComments { get; init; } = ["//"];

        public (string Open, string Close)[] BlockComments { get; init; } = [("/*", "*/")];

        public char[] StringQuotes { get; init; } = ['"', '\''];

        public bool TripleQuotes { get; init; }

        public bool DollarIdentifier { get; init; }
    }

    private static readonly Profile CLike = new()
    {
        Keywords = Split(
            "if else for while do return break continue switch case default goto sizeof typedef struct union enum " +
            "static const extern inline volatile register signed unsigned void class public private protected virtual " +
            "override abstract interface namespace using new delete this base try catch finally throw throws import " +
            "package extends implements instanceof final synchronized transient native super null true false var let " +
            "function async await yield of in is as with from export module require lambda print constexpr noexcept " +
            "template typename auto decltype public internal sealed partial readonly"),
        Types = Split(
            "int long short char float double bool boolean byte uint ulong ushort decimal object string String void " +
            "var dynamic List Dictionary Task IEnumerable Action Func span var"),
        Constants = Split("NULL nullptr true false"),
    };

    private static readonly Profile JsLike = new()
    {
        Keywords = Split(
            "if else for while do return break continue switch case default function var let const class extends " +
            "super new delete typeof instanceof in of throw try catch finally async await yield export import from " +
            "as static get set this null undefined true false void with debugger interface type enum implements " +
            "namespace declare readonly public private protected abstract any unknown never keyof infer satisfies"),
        Types = Split("String Number Boolean Object Array Promise Map Set Symbol BigInt Date RegExp Error JSON Math"),
        Constants = Split("NaN Infinity undefined null true false"),
        DollarIdentifier = true,
    };

    private static readonly Profile Python = new()
    {
        Keywords = Split(
            "and as assert async await break class continue def del elif else except finally for from global if " +
            "import in is lambda nonlocal not or pass raise return try while with yield match case print self"),
        Types = Split(
            "int float str bool list dict tuple set frozenset bytes bytearray complex object type slice range " +
            "enumerate zip map filter sorted sum len min max abs any all input open isinstance issubclass " +
            "property staticmethod classmethod"),
        Constants = Split("True False None NotImplemented Ellipsis"),
        LineComments = ["#"],
        BlockComments = [],
        TripleQuotes = true,
    };

    private static readonly Profile Yaml = new()
    {
        Keywords = [],
        Types = [],
        Constants = Split("true false null yes no on off"),
        LineComments = ["#"],
        BlockComments = [],
    };

    private static readonly Profile Json = new()
    {
        Keywords = [],
        Types = [],
        Constants = Split("true false null"),
        LineComments = [],
        BlockComments = [],
    };

    private static readonly Profile Xml = new()
    {
        Keywords = [],
        Types = [],
        Constants = [],
        LineComments = [],
        BlockComments = [("<!--", "-->")],
    };

    private static readonly Profile Css = new()
    {
        Keywords = Split("important inherit initial unset revert media supports keyframes import charset font-face"),
        Types = [],
        Constants = Split("auto none block inline flex grid absolute relative fixed sticky hidden visible"),
        LineComments = [],
        BlockComments = [("/*", "*/")],
    };

    private static readonly Profile Sql = new()
    {
        Keywords = Split(
            "select from where insert into values update set delete create table alter drop index view join inner " +
            "left right full outer on group by order having limit offset union all distinct as and or not null is " +
            "like between exists case when then else end primary key foreign references default unique check " +
            "constraint begin commit rollback transaction"),
        Types = Split(
            "int integer bigint smallint varchar nvarchar char text date datetime timestamp decimal numeric float " +
            "double boolean blob json uuid"),
        Constants = Split("null true false current_date current_timestamp"),
        LineComments = ["--"],
        BlockComments = [("/*", "*/")],
    };

    private static readonly Profile Shell = new()
    {
        Keywords = Split(
            "if then else elif fi for while do done case esac function return break continue local export readonly " +
            "declare unset shift source alias echo cd exit eval exec trap set"),
        Types = [],
        Constants = Split("true false"),
        LineComments = ["#"],
        BlockComments = [],
        DollarIdentifier = true,
    };

    private static readonly Profile PowerShell = new()
    {
        Keywords = Split(
            "if else elseif switch foreach for while do until function return param begin process end break " +
            "continue try catch finally throw filter class enum using"),
        Types = Split("int string bool array hashtable pscustomobject void char double decimal datetime"),
        Constants = Split("$true $false $null"),
        LineComments = ["#"],
        BlockComments = [("<#", "#>")],
        DollarIdentifier = true,
    };

    private static readonly Profile Plain = new()
    {
        Keywords = [],
        Types = [],
        Constants = [],
        LineComments = [],
        BlockComments = [],
    };

    private static readonly Dictionary<string, Profile> Profiles = new(StringComparer.OrdinalIgnoreCase)
    {
        ["c"] = CLike, ["h"] = CLike, ["cc"] = CLike, ["cpp"] = CLike, ["cxx"] = CLike, ["hpp"] = CLike,
        ["cs"] = CLike, ["csharp"] = CLike, ["java"] = CLike, ["kotlin"] = CLike, ["scala"] = CLike,
        ["dart"] = CLike, ["swift"] = CLike, ["go"] = CLike, ["golang"] = CLike, ["rust"] = CLike, ["rs"] = CLike,
        ["js"] = JsLike, ["javascript"] = JsLike, ["ts"] = JsLike, ["typescript"] = JsLike, ["jsx"] = JsLike,
        ["tsx"] = JsLike,
        ["python"] = Python, ["py"] = Python,
        ["yaml"] = Yaml, ["yml"] = Yaml,
        ["json"] = Json, ["jsonc"] = Json,
        ["xml"] = Xml, ["html"] = Xml, ["htm"] = Xml, ["xaml"] = Xml, ["svg"] = Xml, ["vue"] = Xml,
        ["css"] = Css, ["scss"] = Css, ["less"] = Css,
        ["sql"] = Sql, ["mysql"] = Sql, ["postgres"] = Sql, ["postgresql"] = Sql, ["sqlite"] = Sql,
        ["bash"] = Shell, ["sh"] = Shell, ["shell"] = Shell, ["zsh"] = Shell, ["console"] = Shell,
        ["powershell"] = PowerShell, ["ps1"] = PowerShell,
        ["markdown"] = Plain, ["md"] = Plain, ["text"] = Plain, ["txt"] = Plain, ["console-log"] = Plain,
    };

    private static HashSet<string> Split(string text)
    {
        return new HashSet<string>(text.Split(' ', StringSplitOptions.RemoveEmptyEntries), StringComparer.Ordinal);
    }

    private static Profile? GetProfile(string? language)
    {
        if (string.IsNullOrWhiteSpace(language))
        {
            return null;
        }

        var key = language.Trim().Split(' ', ',', ':', '{')[0];
        return key.Length == 0 ? null : Profiles.TryGetValue(key, out var profile) ? profile : CLike;
    }

    /// <summary>把代码切成带颜色的片段；语言未知时返回单段无色文本。</summary>
    public static List<CodeToken> Tokenize(string code, string? language)
    {
        var profile = GetProfile(language);
        if (profile is null)
        {
            return [new CodeToken(code, null)];
        }

        var tokens = new List<CodeToken>();
        var n = code.Length;
        var i = 0;

        while (i < n)
        {
            var c = code[i];

            var lineComment = profile.LineComments.FirstOrDefault(p => Matches(code, i, p));
            if (!string.IsNullOrEmpty(lineComment))
            {
                var end = code.IndexOf('\n', i);
                end = end < 0 ? n : end;
                tokens.Add(new CodeToken(code[i..end], ColorComment, Italic: true));
                i = end;
                continue;
            }

            var block = profile.BlockComments.FirstOrDefault(b => Matches(code, i, b.Open));
            if (!string.IsNullOrEmpty(block.Open))
            {
                var end = code.IndexOf(block.Close, i + block.Open.Length, StringComparison.Ordinal);
                end = end < 0 ? n : end + block.Close.Length;
                tokens.Add(new CodeToken(code[i..end], ColorComment, Italic: true));
                i = end;
                continue;
            }

            if (profile.TripleQuotes && (Matches(code, i, "\"\"\"") || Matches(code, i, "'''")))
            {
                var quote = code.Substring(i, 3);
                var end = code.IndexOf(quote, i + 3, StringComparison.Ordinal);
                end = end < 0 ? n : end + 3;
                tokens.Add(new CodeToken(code[i..end], ColorString));
                i = end;
                continue;
            }

            if (Array.IndexOf(profile.StringQuotes, c) >= 0)
            {
                var start = i;
                i++;
                while (i < n)
                {
                    if (code[i] == '\\' && i + 1 < n)
                    {
                        i += 2;
                        continue;
                    }

                    if (code[i] == c)
                    {
                        i++;
                        break;
                    }

                    if (code[i] == '\n')
                    {
                        break;
                    }

                    i++;
                }

                tokens.Add(new CodeToken(code[start..i], ColorString));
                continue;
            }

            if (char.IsDigit(c) || (c == '.' && i + 1 < n && char.IsDigit(code[i + 1])))
            {
                var start = i;
                i++;
                while (i < n && (char.IsLetterOrDigit(code[i]) || code[i] is '.' or '_'))
                {
                    i++;
                }

                tokens.Add(new CodeToken(code[start..i], ColorNumber));
                continue;
            }

            if (char.IsLetter(c) || c == '_' || (profile.DollarIdentifier && c == '$') || c == '@')
            {
                var start = i;
                i++;
                while (i < n && (char.IsLetterOrDigit(code[i]) || code[i] is '_' or '$' or '@' or '-'))
                {
                    i++;
                }

                var word = code[start..i];
                string? color = null;
                if (profile.Constants.Contains(word))
                {
                    color = ColorConstant;
                }
                else if (profile.Keywords.Contains(word))
                {
                    color = ColorKeyword;
                }
                else if (profile.Types.Contains(word))
                {
                    color = ColorType;
                }
                else
                {
                    var j = i;
                    while (j < n && code[j] == ' ')
                    {
                        j++;
                    }

                    if (j < n && code[j] == '(')
                    {
                        color = ColorFunction;
                    }
                }

                tokens.Add(new CodeToken(word, color ?? ColorPunct));
                continue;
            }

            if (c == '+' && i + 1 < n && code[i + 1] == ' ')
            {
                tokens.Add(new CodeToken("+", ColorAdd));
                i++;
                continue;
            }

            if (c == '-' && i + 1 < n && code[i + 1] == ' ')
            {
                tokens.Add(new CodeToken("-", ColorDel));
                i++;
                continue;
            }

            tokens.Add(new CodeToken(c.ToString(), ColorPunct));
            i++;
        }

        return tokens;
    }

    private static bool Matches(string text, int index, string token)
    {
        if (token.Length == 0 || index + token.Length > text.Length)
        {
            return false;
        }

        return string.CompareOrdinal(text, index, token, 0, token.Length) == 0;
    }
}
