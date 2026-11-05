using IntuneLogMonitor.Core;

namespace IntuneLogMonitor.Core.Tests;

public class CmTraceParserTests
{
    private const string Line1 =
        "<![LOG[[Win32App] ===Step=== Start to Download content]LOG]!><time=\"09:15:12.3456789\" date=\"11-5-2026\" component=\"AppWorkload\" context=\"\" type=\"1\" thread=\"14\" file=\"\">\r\n";

    private const string MultiLine =
        "<![LOG[[Win32App] Get policies = [{\"Id\":\"a\"},\r\n {\"Id\":\"b\"}]]LOG]!><time=\"09:15:13.0000000\" date=\"11-5-2026\" component=\"AppWorkload\" context=\"\" type=\"2\" thread=\"7\" file=\"AppWorkload.cs:42\">\r\n";

    [Fact]
    public void ParsesSingleEntryWithAllAttributes()
    {
        var e = CmTraceParser.ParseSingle(Line1)!;

        Assert.False(e.IsRaw);
        Assert.Equal("[Win32App] ===Step=== Start to Download content", e.Message);
        Assert.Equal(new DateTime(2026, 11, 5, 9, 15, 12).AddTicks(3456789), e.Timestamp);
        Assert.Equal("AppWorkload", e.Component);
        Assert.Equal("14", e.Thread);
        Assert.Equal(LogSeverity.Info, e.Severity);
    }

    [Fact]
    public void ParsesMultiLineMessage()
    {
        var parser = new CmTraceParser();
        var entries = parser.Feed(Line1 + MultiLine);

        Assert.Equal(2, entries.Count);
        var e = entries[1];
        Assert.Equal("[Win32App] Get policies = [{\"Id\":\"a\"},\r\n {\"Id\":\"b\"}]", e.Message);
        Assert.Equal(LogSeverity.Warning, e.Severity);
        Assert.Equal("AppWorkload.cs:42", e.File);
        Assert.EndsWith(" …", e.MessageFirstLine);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(7)]
    [InlineData(50)]
    public void HandlesArbitraryChunkBoundaries(int chunkSize)
    {
        string text = Line1 + MultiLine + Line1;
        var parser = new CmTraceParser();
        var entries = new List<LogEntry>();
        for (int i = 0; i < text.Length; i += chunkSize)
            entries.AddRange(parser.Feed(text.Substring(i, Math.Min(chunkSize, text.Length - i))));

        Assert.Equal(3, entries.Count);
        Assert.All(entries, e => Assert.False(e.IsRaw));
        Assert.Contains("Get policies", entries[1].Message);
        Assert.Empty(parser.Flush());
    }

    [Fact]
    public void IncompleteEntryIsBufferedUntilComplete()
    {
        var parser = new CmTraceParser();
        Assert.Empty(parser.Feed("<![LOG[halb fertig]LOG]!><time=\"09:00:00.000\" date=\"11-5-2026\" compon"));
        var entries = parser.Feed("ent=\"X\" context=\"\" type=\"3\" thread=\"1\" file=\"\">\r\n");

        var e = Assert.Single(entries);
        Assert.Equal("halb fertig", e.Message);
        Assert.Equal(LogSeverity.Error, e.Severity);
        Assert.Equal("X", e.Component);
    }

    [Fact]
    public void NonCmTraceLinesBecomeRawEntries()
    {
        var parser = new CmTraceParser();
        var entries = parser.Feed("Plain text line\r\n" + Line1 + "another\n");

        Assert.Equal(3, entries.Count);
        Assert.True(entries[0].IsRaw);
        Assert.Equal("Plain text line", entries[0].Message);
        Assert.False(entries[1].IsRaw);
        Assert.True(entries[2].IsRaw);
    }

    [Fact]
    public void FlushEmitsPendingRest()
    {
        var parser = new CmTraceParser();
        Assert.Empty(parser.Feed("<![LOG[abgeschnitten"));
        var rest = parser.Flush();
        Assert.Single(rest);
        Assert.True(rest[0].IsRaw);
    }

    [Theory]
    [InlineData("09:15:12.3456789", "11-5-2026", 2026, 11, 5, 9, 15, 12, 3456789)]
    [InlineData("23:59:59.123-60", "1-31-2025", 2025, 1, 31, 23, 59, 59, 1230000)]   // SCCM-Stil mit Bias
    [InlineData("08:00:01.5+120", "12-24-2026", 2026, 12, 24, 8, 0, 1, 5000000)]
    [InlineData("07:06:05", "2-3-2026", 2026, 2, 3, 7, 6, 5, 0)]
    public void ParsesTimestampVariants(string time, string date, int y, int mo, int d, int h, int mi, int s, long ticks)
    {
        var ts = CmTraceParser.ParseTimestamp(date, time);
        Assert.Equal(new DateTime(y, mo, d, h, mi, s).AddTicks(ticks), ts);
    }

    [Theory]
    [InlineData("", "11-5-2026")]
    [InlineData("xx:yy", "11-5-2026")]
    [InlineData("09:00:00.000", "13-45-2026")]
    public void InvalidTimestampYieldsNull(string time, string date) =>
        Assert.Null(CmTraceParser.ParseTimestamp(date, time));

    [Fact]
    public void GreaterThanInsideQuotedAttributeDoesNotEndEntry()
    {
        var e = CmTraceParser.ParseSingle(
            "<![LOG[msg]LOG]!><time=\"09:00:00.000\" date=\"11-5-2026\" component=\"A>B\" context=\"\" type=\"1\" thread=\"1\" file=\"\">\r\n")!;
        Assert.Equal("A>B", e.Component);
    }
}
