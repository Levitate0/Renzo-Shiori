namespace RenzoBackend.Extensions;

/// <summary>
/// Upper bounds on client-supplied page sizes. A count went straight into
/// <c>.Take(count)</c>, so <c>?count=2147483647</c> made any signed-in user
/// materialise an entire table (the Browse catalogue is ~500k rows) per request.
/// Each ceiling is the largest page a shipping client actually asks for (web and
/// Hub alike), so clamping changes nothing for them.
/// </summary>
public static class PageLimits
{
    /// <summary>Browse / latest catalogue page. Clients ask for 40–60.</summary>
    public const int Latest = 200;

    /// <summary>Updates feed. Both clients fetch 1000 and page locally.</summary>
    public const int Updates = 1000;

    /// <summary>History feed. Already capped at 500 entries server-side.</summary>
    public const int History = 500;

    /// <summary>Download queue lists. Both clients fetch 5000.</summary>
    public const int Downloads = 5000;

    /// <summary>Clamps a requested page size into [0, max].</summary>
    public static int ClampCount(int requested, int max) => Math.Clamp(requested, 0, max);

    /// <summary>Clamps a requested offset to be non-negative.</summary>
    public static int ClampStart(int requested) => Math.Max(0, requested);
}
