namespace TrueNasCommandCenter.Services;

/// <summary>Converts untrusted catalog README HTML into display-safe text.</summary>
public interface ICatalogReadmeSanitizer
{
    /// <summary>Removes markup and decodes safe text for normal Blazor rendering.</summary>
    /// <param name="html">The untrusted catalog README HTML.</param>
    /// <returns>Plain text with readable paragraph boundaries.</returns>
    string Sanitize(string? html);
}
