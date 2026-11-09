using System.Text.RegularExpressions;

namespace MedStock.Web.Services;

/// <summary>
/// Barcodes are stored and compared as the product number. European medicine boxes carry a GS1 DataMatrix with the
/// GTIN (AI 01) plus a serial number that differs per box, so only the GTIN is kept; EAN-13, EAN-8 and UPC-A become the
/// same 14-digit GTIN. Anything else (e.g. a QR code) is kept as trimmed text.
/// </summary>
public static partial class Barcodes
{
    public const int MaxLength = 64;

    public static string? Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        // Drop a symbology identifier ("]d2", "]C1", "]Q3") and control characters such as the GS1 separator.
        var text = SymbologyId().Replace(raw.Trim(), "");
        text = new string(text.Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (text.Length == 0) return null;

        var gs1 = Gs1Gtin().Match(text);
        if (gs1.Success) return gs1.Groups[1].Value;
        if (Digits().IsMatch(text) && text.Length is 8 or 12 or 13 or 14) return text.PadLeft(14, '0');
        return text.Length > MaxLength ? text[..MaxLength] : text;
    }

    [GeneratedRegex(@"^\][A-Za-z][0-9A-Za-z]")]
    private static partial Regex SymbologyId();

    /// <summary>"01" + 14 digits at the start, plain or in human readable form "(01)".</summary>
    [GeneratedRegex(@"^(?:\(01\)|01)(\d{14})(?:\D|\d{2,}|$)")]
    private static partial Regex Gs1Gtin();

    [GeneratedRegex(@"^\d+$")]
    private static partial Regex Digits();
}
