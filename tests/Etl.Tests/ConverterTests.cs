using Etl.Core.Conversion;

namespace Etl.Tests;

/// <summary>Ported case-for-case from tests/test_convert.py: both engines must agree on every value.</summary>
public class ConverterTests
{
    private static readonly ConverterContext Ctx = new(
        codeMaps: new Dictionary<string, IReadOnlyDictionary<string, object?>>
        {
            ["sex"] = new Dictionary<string, object?> { ["1"] = "M", ["2"] = "F", ["m"] = "M", ["male"] = "M" },
        },
        references: new Dictionary<string, IReadOnlySet<string>>
        {
            ["dx"] = new HashSet<string> { "0389", "389", "1000", "01000", "4019", "V5841", "8442" },
        });

    private static Converter Conv(string name, Dictionary<string, object?>? args = null) =>
        ConverterRegistry.Build(name, args, Ctx);

    [Theory]
    [InlineData("20080412")]
    [InlineData("04/12/2008")]
    [InlineData("2008-04-12")]
    [InlineData("4/12/08")]
    [InlineData("4/12/2008")]    // strptime accepts single-digit fields; so must we
    public void DateFormats(string raw) =>
        Assert.Equal(new DateOnly(2008, 4, 12), Conv("date", new() { ["century"] = 2000L })(raw));

    [Fact]
    public void TwoDigitYearWithoutCenturyIsAnError() =>
        Assert.Contains("century", Assert.Throws<ConversionException>(() => Conv("date")("4/12/08")).Message);

    [Fact]
    public void CenturyRule() =>
        Assert.Equal(new DateOnly(1923, 5, 1), Conv("date", new() { ["century"] = 1900L })("5/1/23"));

    [Fact]
    public void BadDate() => Assert.Throws<ConversionException>(() => Conv("date")("20081341"));

    [Theory]
    [InlineData("$1,200.00", "1200.00")]
    [InlineData("1200", "1200.00")]
    [InlineData("  1200.0 ", "1200.00")]
    [InlineData("(50.00)", "-50.00")]
    [InlineData("-50.00", "-50.00")]
    [InlineData("0.00", "0.00")]
    public void Money(string raw, string expected) =>
        Assert.Equal(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), Conv("money")(raw));

    [Fact]
    public void MoneyRejectsGarbage() => Assert.Throws<ConversionException>(() => Conv("money")("12 dollars"));

    [Fact]
    public void IntegerAcceptsDecimalTextButNotFractions()
    {
        Assert.Equal(12L, Conv("integer")("12.0"));
        Assert.Throws<ConversionException>(() => Conv("integer")("12.5"));
    }

    [Fact]
    public void CodeMapCaseInsensitiveFallbackAndUnknown()
    {
        var f = Conv("code", new() { ["map"] = "sex" });
        Assert.Equal("M", f("1"));
        Assert.Equal("M", f("Male"));
        Assert.Equal("M", f("M"));
        Assert.Throws<ConversionException>(() => f("X"));
    }

    [Fact]
    public void Id()
    {
        Assert.Equal("AB12", Conv("id")("  ab12 "));
        Assert.Throws<ConversionException>(() => Conv("id", new() { ["pattern"] = "[0-9A-F]{4}" })("XYZ1"));
    }

    public static TheoryData<object, string?> NdcCases => new()
    {
        { 247037252L, "00247037252" },
        { "00247-0372-52", "00247037252" },
        { "247-372-52", "00247037252" },
        { "OTHER", null },
    };

    [Theory]
    [MemberData(nameof(NdcCases))]
    public void PaddedDigits(object raw, string? expected)
    {
        var f = Conv("padded_digits", new() { ["width"] = 11L, ["segments"] = new List<object?> { 5L, 4L, 2L }, ["placeholders"] = new List<object?> { "OTHER" } });
        Assert.Equal(expected, f(raw));
    }

    [Fact]
    public void PaddedDigitsRejectsUnknownText() =>
        Assert.Throws<ConversionException>(() => Conv("padded_digits", new() { ["width"] = 11L })("UNKNOWN"));

    public static TheoryData<object, string?, string> IcdCases => new()
    {
        { "4019", "4019", "exact" },             // valid as received; '04019' not valid
        { 8442L, "8442", "exact" },              // a long, as SQLite returns from an INTEGER column
        { 389L, null, "ambiguous" },             // '389' and '0389' both valid: never guess
        { 1000L, null, "ambiguous" },
        { "0389", "0389", "exact" },             // zeros intact: nothing to resolve
        { "V58.41", "V5841", "exact" },          // dot stripped
        { 12345L, "12345", "not_in_reference" }, // at max length: no zeros could be lost
        { 77L, null, "unresolved" },             // no candidate valid
    };

    [Theory]
    [MemberData(nameof(IcdCases))]
    public void ZeroPaddedCode(object raw, string? code, string resolution)
    {
        var r = (CodeResult)Conv("zero_padded_code", new() { ["reference"] = "dx", ["max_len"] = 5L })(raw)!;
        Assert.Equal(new CodeResult(code, ConverterRegistry.Str(raw), resolution), r);
    }

    [Fact]
    public void ZeroPaddedCodeRepairsUniqueCandidate()
    {
        var ctx = new ConverterContext(references: new Dictionary<string, IReadOnlySet<string>> { ["dx"] = new HashSet<string> { "0389" } });
        var f = ConverterRegistry.Build("zero_padded_code", new Dictionary<string, object?> { ["reference"] = "dx", ["max_len"] = 5L }, ctx);
        Assert.Equal(new CodeResult("0389", "389", "repaired"), f(389L));
    }

    [Fact]
    public void AssumeReceivedStoresLabeledGuessOnlyWhenReceivedValueIsValid()
    {
        var args = new Dictionary<string, object?> { ["reference"] = "dx", ["max_len"] = 5L, ["on_ambiguous"] = "assume_received" };
        Assert.Equal(new CodeResult("389", "389", "assumed"), Conv("zero_padded_code", args)(389L));
        var ctx = new ConverterContext(references: new Dictionary<string, IReadOnlySet<string>> { ["dx"] = new HashSet<string> { "0389", "00389" } });
        Assert.Equal(new CodeResult(null, "389", "ambiguous"), ConverterRegistry.Build("zero_padded_code", args, ctx)(389L));
    }

    [Fact]
    public void OnAmbiguousRejectsUnknownPolicy() =>
        Assert.Throws<ArgumentException>(() => Conv("zero_padded_code", new() { ["reference"] = "dx", ["max_len"] = 5L, ["on_ambiguous"] = "pad" }));

    [Fact]
    public void StrMatchesPythonForSqliteTypes()
    {
        Assert.Equal("389", ConverterRegistry.Str(389L));
        Assert.Equal("458.0", ConverterRegistry.Str(458.0));   // Python str(458.0)
        Assert.Equal("1.5", ConverterRegistry.Str(1.5));
    }
}
