namespace RevitMCP.Contracts;

/// <summary>
/// Response-size policy. Revit models routinely hold hundreds of thousands of elements;
/// every list command is bounded and pages with an opaque offset cursor.
/// </summary>
public static class Paging
{
    public const int DefaultLimit = 200;
    public const int MaxLimit = 2000;

    public static int Clamp(int? requested) =>
        requested is null or <= 0 ? DefaultLimit : Math.Min(requested.Value, MaxLimit);

    /// <summary>Encodes an integer offset as an opaque cursor so callers do not do arithmetic on it.</summary>
    public static string EncodeCursor(int nextOffset) =>
        Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"o:{nextOffset}"));

    public static bool TryDecodeCursor(string? cursor, out int offset)
    {
        offset = 0;
        if (string.IsNullOrEmpty(cursor)) return true;   // absent cursor == start
        try
        {
            var s = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(cursor));
            return s.StartsWith("o:", StringComparison.Ordinal)
                && int.TryParse(s.AsSpan(2), out offset)
                && offset >= 0;
        }
        catch (FormatException) { return false; }
    }
}
