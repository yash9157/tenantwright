using System.Net;
using System.Text.RegularExpressions;

namespace Notifications.Application.Delivery;

internal static partial class NotificationTextFormatter
{
    public static string ToPlainText(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return string.Empty;
        }

        var withoutTags = HtmlTagRegex().Replace(input, " ");
        var decoded = WebUtility.HtmlDecode(withoutTags);
        var normalized = MultiSpaceRegex().Replace(decoded, " ").Trim();
        return normalized;
    }

    /// <summary>
    /// Produces newline-preserving safe text from (possibly HTML) template output. Block-level
    /// boundaries become line breaks, remaining tags are stripped, HTML entities are decoded, and
    /// only intra-line whitespace is collapsed. The result contains no markup, so it is safe to
    /// render as text without any HTML sanitization. Consumers should render it with a
    /// whitespace-preserving style (e.g. CSS <c>white-space: pre-wrap</c>).
    /// </summary>
    public static string ToStructuredText(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return string.Empty;
        }

        // Normalize newlines then convert block-level boundaries to newlines before stripping tags.
        var working = input.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        working = BlockBoundaryRegex().Replace(working, "\n");

        var withoutTags = HtmlTagRegex().Replace(working, string.Empty);
        var decoded = WebUtility.HtmlDecode(withoutTags);

        // Collapse spaces/tabs within a line but keep line breaks.
        var lines = decoded.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            lines[i] = IntraLineSpaceRegex().Replace(lines[i], " ").Trim();
        }

        // Join, then cap runs of blank lines to a single blank line and trim outer blank lines.
        var joined = string.Join("\n", lines);
        joined = MultiBlankLineRegex().Replace(joined, "\n\n");
        return joined.Trim('\n', ' ');
    }

    [GeneratedRegex("<.*?>", RegexOptions.Singleline | RegexOptions.Compiled)]
    private static partial Regex HtmlTagRegex();

    [GeneratedRegex("\\s+", RegexOptions.Compiled)]
    private static partial Regex MultiSpaceRegex();

    // Closing/standalone block-level tags that should map to a line break.
    [GeneratedRegex("</?(p|div|br|li|tr|h[1-6]|ul|ol|table|section|article|header|footer|blockquote)\\s*/?>", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex BlockBoundaryRegex();

    [GeneratedRegex("[ \\t\\f\\v]+", RegexOptions.Compiled)]
    private static partial Regex IntraLineSpaceRegex();

    [GeneratedRegex("\\n{3,}", RegexOptions.Compiled)]
    private static partial Regex MultiBlankLineRegex();
}
