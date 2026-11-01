using System.Globalization;
using System.Net;
using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Math;

namespace MarkdownToWord.Core.Html;

/// <summary>按 Microsoft 365 MathML 3 Presentation MathML 规范输出剪贴板公式。</summary>
internal static class OmmlToMathML
{
    private const string MathNamespace = "http://www.w3.org/1998/Math/MathML";
    private const string OfficeMathNamespace = "http://schemas.openxmlformats.org/officeDocument/2006/math";
    private const string InvisibleFunction = "\u2061";
    private static readonly HashSet<string> MovableLimitOperators = new(StringComparer.Ordinal)
    {
        "lim", "limsup", "liminf", "max", "min", "sup", "inf", "det", "dim", "ker", "deg",
        "gcd", "hom", "arg", "Pr", "injlim", "projlim",
    };

    public static string Convert(OfficeMath math, bool display, double fontSizePt)
    {
        ArgumentNullException.ThrowIfNull(math);

        var body = ConvertChildren(math);
        var displayAttribute = display ? " display=\"block\"" : string.Empty;
        var size = Math.Clamp(fontSizePt, 6, 36).ToString("0.##", CultureInfo.InvariantCulture);
        return $"<math xmlns=\"{MathNamespace}\"{displayAttribute} mathsize=\"{size}pt\">{body}</math>";
    }

    private static string ConvertChildren(OpenXmlElement element)
    {
        var result = new StringBuilder();
        foreach (var child in element.ChildElements)
        {
            result.Append(ConvertNode(child));
        }

        return result.ToString();
    }

    private static string ConvertNode(OpenXmlElement element)
    {
        return element.LocalName switch
        {
            "oMath" => ConvertChildren(element),
            "e" or "num" or "den" or "sub" or "sup" or "lim" or "fName" or "deg" => ConvertChildren(element),
            "r" => ConvertRun(element),
            "t" => Escape(element.InnerText),
            "f" => ConvertFraction(element),
            "rad" => ConvertRadical(element),
            "sSub" => ConvertSubscript(element),
            "sSup" => ConvertSuperscript(element),
            "sSubSup" => ConvertSubSuperscript(element),
            "nary" => ConvertNary(element),
            "d" => ConvertDelimiter(element),
            "acc" => ConvertAccent(element),
            "bar" => ConvertBar(element),
            "groupChr" => ConvertGroupCharacter(element),
            "limLow" => ConvertLimit(element, upper: false),
            "limUpp" => ConvertLimit(element, upper: true),
            "func" => ConvertFunction(element),
            "m" => ConvertMatrix(element),
            "eqArr" => ConvertEquationArray(element),
            "borderBox" => $"<menclose notation=\"box\">{Child(element, "e")}</menclose>",
            "phant" => $"<mphantom>{Child(element, "e")}</mphantom>",
            "box" or "argPr" => ConvertChildren(element),
            _ when element.LocalName.EndsWith("Pr", StringComparison.Ordinal) => string.Empty,
            _ => ConvertChildren(element),
        };
    }

    private static string ConvertRun(OpenXmlElement run)
    {
        var text = ChildText(run, "t");
        if (text.Length == 0)
        {
            return string.Empty;
        }

        var properties = FirstChild(run, "rPr");
        var normal = FirstChild(properties, "nor") is not null;
        var style = Attribute(FirstChild(properties, "sty"), "val");
        var variant = style switch
        {
            "b" => "bold",
            "i" => "italic",
            "bi" => "bold-italic",
            "p" => "normal",
            _ => null,
        };

        if (text.All(char.IsWhiteSpace))
        {
            return $"<mtext xml:space=\"preserve\">{Escape(text)}</mtext>";
        }

        if (IsNumber(text))
        {
            return WrapToken("mn", variant, text);
        }

        if (IsOperator(text))
        {
            return WrapToken("mo", variant, text);
        }

        if (normal || variant == "normal")
        {
            var token = text.Length > 1 && (text.Any(char.IsWhiteSpace) || text.Any(IsCjk))
                ? "mtext"
                : "mi";
            return WrapToken(token, "normal", text);
        }

        return WrapToken("mi", variant, text);
    }

    private static string ConvertFraction(OpenXmlElement fraction)
    {
        var type = Attribute(FirstChild(FirstChild(fraction, "fPr"), "type"), "val");
        var noBar = type.Equals("noBar", StringComparison.OrdinalIgnoreCase);
        var bar = noBar ? " linethickness=\"0\"" : string.Empty;
        return $"<mfrac{bar}>{Content(fraction, "num")}{Content(fraction, "den")}</mfrac>";
    }

    private static string ConvertRadical(OpenXmlElement radical)
    {
        var body = Content(radical, "e");
        var hideDegree = Attribute(FirstChild(FirstChild(radical, "radPr"), "degHide"), "val");
        var degreeElement = FirstChild(radical, "deg");
        var degree = Content(radical, "deg");
        return hideDegree is "1" or "true" || degreeElement?.ChildElements.Any() != true
            ? $"<msqrt>{body}</msqrt>"
            : $"<mroot>{body}{degree}</mroot>";
    }

    private static string ConvertSubscript(OpenXmlElement element) =>
        $"<msub>{Content(element, "e")}{Content(element, "sub")}</msub>";

    private static string ConvertSuperscript(OpenXmlElement element) =>
        $"<msup>{Content(element, "e")}{Content(element, "sup")}</msup>";

    private static string ConvertSubSuperscript(OpenXmlElement element) =>
        $"<msubsup>{Content(element, "e")}{Content(element, "sub")}{Content(element, "sup")}</msubsup>";

    private static string ConvertNary(OpenXmlElement nary)
    {
        var properties = FirstChild(nary, "naryPr");
        var chr = Attribute(FirstChild(properties, "chr"), "val");
        if (chr.Length == 0)
        {
            chr = "\u2211";
        }

        var underOver = Attribute(FirstChild(properties, "limLoc"), "val")
            .Equals("undOvr", StringComparison.OrdinalIgnoreCase);
        var subHidden = Attribute(FirstChild(properties, "subHide"), "val") is "1" or "true";
        var supHidden = Attribute(FirstChild(properties, "supHide"), "val") is "1" or "true";
        var sub = subHidden ? string.Empty : Content(nary, "sub");
        var sup = supHidden ? string.Empty : Content(nary, "sup");
        var body = Content(nary, "e");
        var op = $"<mo>{Escape(chr)}</mo>";

        if (sub.Length > 0 && sup.Length > 0)
        {
            return underOver
                ? $"<munderover>{op}{sub}{sup}</munderover>{body}"
                : $"<msubsup>{op}{sub}{sup}</msubsup>{body}";
        }

        if (sub.Length > 0)
        {
            return underOver ? $"<munder>{op}{sub}</munder>{body}" : $"<msub>{op}{sub}</msub>{body}";
        }

        if (sup.Length > 0)
        {
            return underOver ? $"<mover>{op}{sup}</mover>{body}" : $"<msup>{op}{sup}</msup>{body}";
        }

        return op + body;
    }

    private static string ConvertDelimiter(OpenXmlElement delimiter)
    {
        var properties = FirstChild(delimiter, "dPr");
        var begin = Attribute(FirstChild(properties, "begChr"), "val");
        var end = Attribute(FirstChild(properties, "endChr"), "val");
        var separator = Attribute(FirstChild(properties, "sepChr"), "val");
        var content = delimiter.ChildElements
            .Where(child => child.LocalName == "e")
            .Select(ConvertNode)
            .Where(value => value.Length > 0)
            .ToArray();

        var body = new StringBuilder("<mrow>");
        if (begin.Length > 0)
        {
            body.Append("<mo>").Append(Escape(begin)).Append("</mo>");
        }

        for (var i = 0; i < content.Length; i++)
        {
            if (i > 0)
            {
                body.Append("<mo>").Append(Escape(separator.Length == 0 ? "," : separator)).Append("</mo>");
            }

            body.Append(content[i]);
        }

        if (end.Length > 0)
        {
            body.Append("<mo>").Append(Escape(end)).Append("</mo>");
        }

        return body.Append("</mrow>").ToString();
    }

    private static string ConvertAccent(OpenXmlElement accent)
    {
        var chr = Attribute(FirstChild(FirstChild(accent, "accPr"), "chr"), "val");
        if (chr.Length == 0)
        {
            chr = "\u0302";
        }

        return $"<mover accent=\"true\">{Content(accent, "e")}<mo>{Escape(chr)}</mo></mover>";
    }

    private static string ConvertBar(OpenXmlElement bar)
    {
        var bottom = Attribute(FirstChild(FirstChild(bar, "barPr"), "pos"), "val")
            .Equals("bot", StringComparison.OrdinalIgnoreCase);
        return bottom
            ? $"<munder>{Content(bar, "e")}<mo>\u00AF</mo></munder>"
            : $"<mover>{Content(bar, "e")}<mo>\u00AF</mo></mover>";
    }

    private static string ConvertGroupCharacter(OpenXmlElement group)
    {
        var properties = FirstChild(group, "groupChrPr");
        var chr = Attribute(FirstChild(properties, "chr"), "val");
        var bottom = Attribute(FirstChild(properties, "pos"), "val")
            .Equals("bot", StringComparison.OrdinalIgnoreCase);
        return bottom
            ? $"<munder>{Content(group, "e")}<mo>{Escape(chr)}</mo></munder>"
            : $"<mover>{Content(group, "e")}<mo>{Escape(chr)}</mo></mover>";
    }

    private static string ConvertLimit(OpenXmlElement limit, bool upper)
    {
        var bodyElement = FirstChild(limit, "e");
        var bodyText = bodyElement?.InnerText.Trim() ?? string.Empty;
        var body = MovableLimitOperators.Contains(bodyText)
            ? $"<mo movablelimits=\"true\" form=\"prefix\">{Escape(bodyText)}</mo>"
            : Content(limit, "e");
        return upper
            ? $"<mover>{body}{Content(limit, "lim")}</mover>"
            : $"<munder>{body}{Content(limit, "lim")}</munder>";
    }

    private static string ConvertFunction(OpenXmlElement function)
    {
        return $"<mrow>{Content(function, "fName")}<mo>{InvisibleFunction}</mo>{Content(function, "e")}</mrow>";
    }

    private static string ConvertMatrix(OpenXmlElement matrix)
    {
        var rows = new StringBuilder("<mtable>");
        foreach (var row in matrix.ChildElements.Where(child => child.LocalName == "mr"))
        {
            rows.Append("<mtr>");
            foreach (var cell in row.ChildElements.Where(child => child.LocalName == "e"))
            {
                rows.Append("<mtd>").Append(ConvertNode(cell)).Append("</mtd>");
            }

            rows.Append("</mtr>");
        }

        return rows.Append("</mtable>").ToString();
    }

    private static string ConvertEquationArray(OpenXmlElement array)
    {
        var rows = new StringBuilder("<mtable columnalign=\"left\">");
        foreach (var row in array.ChildElements.Where(child => child.LocalName == "e"))
        {
            rows.Append("<mtr><mtd>").Append(ConvertNode(row)).Append("</mtd></mtr>");
        }

        return rows.Append("</mtable>").ToString();
    }

    private static string Child(OpenXmlElement element, string name)
    {
        var child = element.ChildElements.FirstOrDefault(item => item.LocalName == name);
        return child is null ? string.Empty : ConvertNode(child);
    }

    private static string Content(OpenXmlElement element, string name)
    {
        var child = element.ChildElements.FirstOrDefault(item => item.LocalName == name);
        if (child is null)
        {
            return "<mrow></mrow>";
        }

        var parts = child.ChildElements
            .Select(ConvertNode)
            .Where(value => value.Length > 0)
            .ToArray();
        return parts.Length switch
        {
            0 => "<mrow></mrow>",
            1 => parts[0],
            _ => $"<mrow>{string.Concat(parts)}</mrow>",
        };
    }

    private static string ChildText(OpenXmlElement element, string name)
    {
        return element.ChildElements.FirstOrDefault(item => item.LocalName == name)?.InnerText ?? string.Empty;
    }

    private static OpenXmlElement? FirstChild(OpenXmlElement? element, string name)
    {
        return element?.ChildElements.FirstOrDefault(item => item.LocalName == name);
    }

    private static string Attribute(OpenXmlElement? element, string name)
    {
        return element?.GetAttribute(name, OfficeMathNamespace).Value ?? string.Empty;
    }

    private static string WrapToken(string name, string? variant, string text)
    {
        var variantAttribute = string.IsNullOrEmpty(variant)
            ? string.Empty
            : $" mathvariant=\"{variant}\"";
        return $"<{name}{variantAttribute}>{Escape(text)}</{name}>";
    }

    private static bool IsNumber(string text) =>
        text.Any(char.IsDigit) &&
        text.All(ch => char.IsDigit(ch) || ch is '.' or ',' or '\u2212');

    private static bool IsOperator(string text)
    {
        if (text.Length != 1)
        {
            return false;
        }

        return char.IsPunctuation(text[0]) ||
               char.IsSymbol(text[0]) ||
               text[0] is '+' or '-' or '=' or '<' or '>' or '/' or '|' or '\u2212';
    }

    private static bool IsCjk(char value) =>
        value is >= '\u2E80' and <= '\u9FFF' or >= '\uF900' and <= '\uFAFF';

    private static string Escape(string text) => WebUtility.HtmlEncode(text);
}
