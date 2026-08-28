using System.Net;
using System.Text.RegularExpressions;

namespace TrueNasCommandCenter.Services;

/// <inheritdoc />
public sealed class CatalogReadmeSanitizer : ICatalogReadmeSanitizer
{
    private const int MaximumInputLength = 500_000;
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);

    /// <inheritdoc />
    public string Sanitize(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return string.Empty;
        }

        var safeLength = Math.Min(html.Length, MaximumInputLength);
        var text = html[..safeLength];
        text = Regex.Replace(text, "<!--[\\s\\S]*?-->", string.Empty, RegexOptions.CultureInvariant, RegexTimeout);
        text = Regex.Replace(text, "<(script|style|template|svg|math)\\b[^>]*>[\\s\\S]*?</\\1\\s*>", string.Empty, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout);
        text = Regex.Replace(text, "<\\s*(br|hr)\\s*/?\\s*>", "\n", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout);
        text = Regex.Replace(text, "<\\s*/\\s*(p|div|li|h[1-6]|tr|section|article|blockquote)\\s*>", "\n", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout);
        text = Regex.Replace(text, "<[^>]*>", string.Empty, RegexOptions.CultureInvariant, RegexTimeout);
        text = WebUtility.HtmlDecode(text).Replace('\u00a0', ' ');
        text = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        text = Regex.Replace(text, "[ \\t]+\n", "\n", RegexOptions.CultureInvariant, RegexTimeout);
        text = Regex.Replace(text, "\n{3,}", "\n\n", RegexOptions.CultureInvariant, RegexTimeout);
        return text.Trim();
    }
}
