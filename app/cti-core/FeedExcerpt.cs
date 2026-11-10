using System.Globalization;
using System.Text;
using System.Xml.Linq;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;

namespace CtiCore;

internal sealed class FeedExcerpt
{
    internal const int MaximumLength = 360;
    internal const int MaximumMarkup = 8192;
    // Parse locally without a resource loader or scripting extension. Only plain text leaves this class.
    private readonly HtmlParser parser = new(new HtmlParserOptions { IsScripting = false });
    private static readonly HashSet<string> Hidden = new(StringComparer.Ordinal)
    {
        "script", "style", "template", "noscript", "iframe", "object", "embed", "svg", "math",
        "form", "button", "textarea", "select"
    };
    private static readonly HashSet<string> Blocks = new(StringComparer.Ordinal)
    {
        "p", "br", "div", "li", "ul", "ol", "hr", "section", "article", "blockquote",
        "h1", "h2", "h3", "h4", "h5", "h6", "pre", "dl", "dt", "dd", "tr", "td", "th"
    };

    public string? Read(XElement? description)
    {
        if (description is null) return null;
        string markup = description.HasElements
            ? string.Concat(description.Nodes().Select(node => node.ToString(SaveOptions.DisableFormatting)))
            : description.Value;
        if (string.IsNullOrWhiteSpace(markup)) return null;
        if (markup.Length > MaximumMarkup)
        {
            int end = char.IsHighSurrogate(markup[MaximumMarkup - 1]) ? MaximumMarkup - 1 : MaximumMarkup;
            markup = markup[..end];
        }

        using var document = parser.ParseDocument(markup);
        if (document.Body is not { } body) return null;
        var visible = new StringBuilder(markup.Length);
        var pending = new Stack<(INode Node, bool Break)>();
        pending.Push((body, false));
        while (pending.TryPop(out var step))
        {
            if (step.Break)
            {
                visible.Append(' ');
                continue;
            }
            if (step.Node is IElement element)
            {
                if (Hidden.Contains(element.LocalName) || element.HasAttribute("hidden")
                    || string.Equals(element.GetAttribute("aria-hidden")?.Trim(), "true", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (Blocks.Contains(element.LocalName))
                {
                    visible.Append(' ');
                    pending.Push((element, true));
                }
            }
            if (step.Node.NodeType == NodeType.Text) visible.Append(step.Node.TextContent);
            for (var child = step.Node.LastChild; child is not null; child = child.PreviousSibling)
                pending.Push((child, false));
        }

        var normalized = new StringBuilder(visible.Length);
        bool space = false;
        foreach (var rune in visible.ToString().EnumerateRunes())
        {
            if (Rune.IsWhiteSpace(rune))
            {
                space = normalized.Length != 0;
                continue;
            }
            if (Rune.GetUnicodeCategory(rune) is UnicodeCategory.Control or UnicodeCategory.Format) continue;
            if (space) normalized.Append(' ');
            normalized.Append(rune.ToString());
            space = false;
        }
        if (normalized.Length == 0) return null;

        string result = normalized.ToString();
        if (result.Length <= MaximumLength) return result;
        int cut = 0;
        while (cut < MaximumLength - 1)
        {
            int size = StringInfo.GetNextTextElementLength(result.AsSpan(cut));
            if (cut + size > MaximumLength - 1) break;
            cut += size;
        }
        if (cut == 0) return null;
        int wordBreak = result.LastIndexOf(' ', cut);
        if (wordBreak > MaximumLength * 3 / 4) cut = wordBreak;
        return result[..cut].TrimEnd() + "…";
    }
}
