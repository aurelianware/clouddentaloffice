namespace CloudDentalOffice.EdiCommon;

/// <summary>
/// Parses raw X12 EDI content into segments and elements.
/// Clean-room implementation for dental X12 transactions.
/// </summary>
public class X12Parser
{
    public char ElementSeparator { get; private set; } = '*';
    public char SegmentTerminator { get; private set; } = '~';
    public char? SubElementSeparator { get; private set; } = ':';

    public X12Document Parse(string rawX12)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rawX12);

        // Detect delimiters from ISA segment (fixed-width: ISA*...*...*...*...*...*...*...*...*...*...*...*...*...*...*...*<sub>~)
        if (rawX12.Length < 106 || !rawX12.StartsWith("ISA"))
            throw new X12ParseException("Invalid X12: Missing or malformed ISA segment");

        ElementSeparator = rawX12[3];
        SubElementSeparator = rawX12[104];
        SegmentTerminator = rawX12[105];

        var segments = rawX12
            .Split(SegmentTerminator)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => ParseSegment(s.Trim()))
            .ToList();

        return new X12Document
        {
            Segments = segments,
            ElementSeparator = ElementSeparator,
            SegmentTerminator = SegmentTerminator,
            SubElementSeparator = SubElementSeparator
        };
    }

    private X12Segment ParseSegment(string raw)
    {
        var elements = raw.Split(ElementSeparator);
        return new X12Segment
        {
            SegmentId = elements[0],
            Elements = elements.Skip(1).ToList(),
            Raw = raw
        };
    }
}

public class X12Document
{
    public List<X12Segment> Segments { get; init; } = [];
    public char ElementSeparator { get; init; }
    public char SegmentTerminator { get; init; }
    public char? SubElementSeparator { get; init; }

    public IEnumerable<X12Segment> GetSegments(string segmentId) =>
        Segments.Where(s => s.SegmentId.Equals(segmentId, StringComparison.OrdinalIgnoreCase));

    public X12Segment? GetFirstSegment(string segmentId) =>
        Segments.FirstOrDefault(s => s.SegmentId.Equals(segmentId, StringComparison.OrdinalIgnoreCase));
}

public class X12Segment
{
    public string SegmentId { get; init; } = string.Empty;
    public List<string> Elements { get; init; } = [];
    public string Raw { get; init; } = string.Empty;

    /// <summary>Gets element at 1-based position (matching X12 spec notation).</summary>
    public string? GetElement(int position) =>
        position > 0 && position <= Elements.Count ? Elements[position - 1] : null;
}

public class X12ParseException(string message) : Exception(message);
