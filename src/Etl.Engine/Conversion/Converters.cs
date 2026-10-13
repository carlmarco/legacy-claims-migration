using System.Globalization;
using System.Text.RegularExpressions;

namespace Etl.Core.Conversion;

/// <summary>A value the converter can't interpret. The engine routes the row to quarantine; it never guesses.</summary>
public sealed class ConversionException(string message) : Exception(message);

/// <summary>A compiled converter: legacy value in, typed target value out (null = SQL NULL).</summary>
public delegate object? Converter(object value);

/// <summary>Output of a code resolver: three target columns (col, col_raw, col_resolution).</summary>
public sealed record CodeResult(string? Code, string Raw, string Resolution);

/// <summary>Read-only lookups shared by converters: code maps and reference code sets.</summary>
public sealed class ConverterContext(
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, object?>>? codeMaps = null,
    IReadOnlyDictionary<string, IReadOnlySet<string>>? references = null)
{
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, object?>> CodeMaps { get; } =
        codeMaps ?? new Dictionary<string, IReadOnlyDictionary<string, object?>>();
    public IReadOnlyDictionary<string, IReadOnlySet<string>> References { get; } =
        references ?? new Dictionary<string, IReadOnlySet<string>>();
}

/// <summary>
/// Named converter factories. A mapping spec references converters by name; a factory turns
/// (args, context) into a <see cref="Converter"/> once, at compile time, so per-row work is a delegate call.
/// Behavior mirrors the Python engine (src/etl/convert.py) exactly; parity is tested.
/// </summary>
public static class ConverterRegistry
{
    private static readonly Dictionary<string, Func<IReadOnlyDictionary<string, object?>, ConverterContext, Converter>> Factories = new()
    {
        ["text"] = (_, _) => Text,
        ["id"] = Id,
        ["integer"] = (_, _) => Integer,
        ["decimal"] = (_, _) => DecimalValue,
        ["money"] = (_, _) => Money,
        ["date"] = Date,
        ["code"] = Code,
        ["padded_digits"] = PaddedDigits,
        ["zero_padded_code"] = ZeroPaddedCode,
    };

    public static IReadOnlyCollection<string> Names => Factories.Keys;

    /// <summary>Column-name suffixes a converter emits: most emit one value; code resolvers emit three.</summary>
    public static IReadOnlyList<string> Outputs(string name) =>
        name == "zero_padded_code" ? ["", "_raw", "_resolution"] : [""];

    public static Converter Build(string name, IReadOnlyDictionary<string, object?>? args, ConverterContext ctx)
    {
        if (!Factories.TryGetValue(name, out var factory))
            throw new KeyNotFoundException($"unknown converter '{name}'; known: {string.Join(", ", Factories.Keys.Order())}");
        return factory(args ?? new Dictionary<string, object?>(), ctx);
    }

    // ---- helpers ------------------------------------------------------------------------------

    /// <summary>Python's str(v) for the value types SQLite returns, so both engines see the same text.</summary>
    public static string Str(object v) => v switch
    {
        string s => s,
        long l => l.ToString(CultureInfo.InvariantCulture),
        int i => i.ToString(CultureInfo.InvariantCulture),
        double d when d == Math.Floor(d) && !double.IsInfinity(d) => d.ToString("F1", CultureInfo.InvariantCulture),
        double d => d.ToString("R", CultureInfo.InvariantCulture),
        decimal m => m.ToString(CultureInfo.InvariantCulture),
        _ => Convert.ToString(v, CultureInfo.InvariantCulture) ?? "",
    };

    private static bool IsAllDigits(string s) => s.Length > 0 && s.All(char.IsAsciiDigit);

    private static string Repr(object v) => v is string s ? $"'{s}'" : Str(v);

    private static T Arg<T>(IReadOnlyDictionary<string, object?> args, string key, T fallback) =>
        args.TryGetValue(key, out var v) && v is not null ? (T)Convert.ChangeType(v, typeof(T), CultureInfo.InvariantCulture) : fallback;

    private static T Required<T>(IReadOnlyDictionary<string, object?> args, string key) =>
        args.TryGetValue(key, out var v) && v is not null
            ? (T)Convert.ChangeType(v, typeof(T), CultureInfo.InvariantCulture)
            : throw new ArgumentException($"converter argument '{key}' is required");

    private static List<object?> List(IReadOnlyDictionary<string, object?> args, string key) =>
        args.TryGetValue(key, out var v) && v is IEnumerable<object?> e ? e.ToList() : [];

    // ---- scalars ------------------------------------------------------------------------------

    private static object? Text(object v)
    {
        var s = Str(v).Trim();
        return s.Length == 0 ? null : s;
    }

    private static Converter Id(IReadOnlyDictionary<string, object?> args, ConverterContext _)
    {
        var pattern = args.TryGetValue("pattern", out var p) && p is string ps ? new Regex($"^(?:{ps})$", RegexOptions.CultureInvariant) : null;
        return v =>
        {
            var s = Str(v).Trim().ToUpperInvariant();
            if (pattern is not null && !pattern.IsMatch(s))
                throw new ConversionException($"id {Repr(v)} does not match {p}");
            return s;
        };
    }

    private static decimal ParseDecimal(object v, string what)
    {
        var s = Str(v).Trim();
        if (decimal.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) return d;
        throw new ConversionException($"not {what}: {Repr(v)}");
    }

    private static object? Integer(object v)
    {
        var d = ParseDecimal(v, "a number");
        if (d != decimal.Truncate(d)) throw new ConversionException($"not a whole number: {Repr(v)}");
        return (long)d;
    }

    private static object? DecimalValue(object v) => ParseDecimal(v, "a number");

    /// <summary>'$1,200.00', ' 1200.0 ', '(50.00)' -> decimal rounded to cents (banker's rounding, like Python's quantize).</summary>
    private static object? Money(object v)
    {
        var s = Str(v).Trim().Replace("$", "").Replace(",", "").Trim();
        var negative = s.StartsWith('(') && s.EndsWith(')');
        if (negative) s = s[1..^1];
        if (!decimal.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
            throw new ConversionException($"not money: {Repr(v)}");
        // + 0.00m forces scale 2 ("60" -> "60.00") without changing the value; Round alone never adds decimal places.
        return Math.Round(negative ? -d : d, 2, MidpointRounding.ToEven) + 0.00m;
    }

    private static readonly Regex TwoDigitYear = new(@"^(\d{1,2})/(\d{1,2})/(\d{2})$", RegexOptions.Compiled);

    /// <summary>Translate a strptime pattern to .NET. Fields separated by punctuation accept 1-2 digits, like strptime.</summary>
    private static string[] DotNetFormats(string python)
    {
        var strict = python.Replace("%Y", "yyyy").Replace("%m", "MM").Replace("%d", "dd");
        var loose = python.Replace("%Y", "yyyy").Replace("%m", "M").Replace("%d", "d");
        return python.Contains("%m%d") || python.Contains("%Y%m") ? [strict] : [strict, loose];
    }

    private static Converter Date(IReadOnlyDictionary<string, object?> args, ConverterContext _)
    {
        var python = List(args, "formats").Select(f => (string)f!).ToList();
        if (python.Count == 0) python = ["%Y%m%d", "%m/%d/%Y", "%Y-%m-%d"];
        var formats = python.SelectMany(DotNetFormats).ToArray();
        long? century = args.TryGetValue("century", out var c) && c is not null ? Convert.ToInt64(c, CultureInfo.InvariantCulture) : null;
        return v =>
        {
            var s = Str(v).Trim();
            var m = TwoDigitYear.Match(s);
            if (m.Success)
            {
                if (century is null) throw new ConversionException($"two-digit year with no century rule: {Repr(v)}");
                try { return new DateOnly((int)century + int.Parse(m.Groups[3].Value), int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value)); }
                catch (ArgumentOutOfRangeException) { throw new ConversionException($"bad date: {Repr(v)}"); }
            }
            if (DateOnly.TryParseExact(s, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) return d;
            throw new ConversionException($"unrecognized date: {Repr(v)}");
        };
    }

    /// <summary>Map through a named code map. Unknown codes are an error, never passed through.</summary>
    private static Converter Code(IReadOnlyDictionary<string, object?> args, ConverterContext ctx)
    {
        var name = Required<string>(args, "map");
        var table = ctx.CodeMaps[name];
        return v =>
        {
            var key = Str(v).Trim();
            if (table.TryGetValue(key, out var hit)) return hit;
            if (table.TryGetValue(key.ToLowerInvariant(), out hit)) return hit;
            throw new ConversionException($"code {Repr(v)} not in map '{name}'");
        };
    }

    /// <summary>Fixed-width numeric identifiers that lost leading zeros (NDC, ZIP...). Known placeholders become NULL.</summary>
    private static Converter PaddedDigits(IReadOnlyDictionary<string, object?> args, ConverterContext _)
    {
        var width = (int)Required<long>(args, "width");
        var segments = List(args, "segments").Select(x => Convert.ToInt32(x, CultureInfo.InvariantCulture)).ToArray();
        var placeholders = List(args, "placeholders").Select(x => ((string)x!).ToUpperInvariant()).ToHashSet();
        return v =>
        {
            var s = Str(v).Trim();
            if (placeholders.Contains(s.ToUpperInvariant())) return null;
            if (s.Contains('-') && segments.Length > 0)
            {
                var parts = s.Split('-');
                if (parts.Length != segments.Length || !parts.All(IsAllDigits))
                    throw new ConversionException($"bad segmented code: {Repr(v)}");
                s = string.Concat(parts.Zip(segments, (part, w) => part.PadLeft(w, '0')));
            }
            if (!IsAllDigits(s) || s.Length > width) throw new ConversionException($"bad fixed-width code: {Repr(v)}");
            return s.PadLeft(width, '0');
        };
    }

    /// <summary>
    /// Resolve a code that may have lost leading zeros (stored in an INTEGER column). Candidates are the received
    /// digits plus left-padded variants up to max_len, checked against a reference set. One valid -> use it.
    /// Several -> ambiguous: withheld, or with on_ambiguous=assume_received, the received value if it's valid
    /// (labeled 'assumed'). Never an unlabeled guess. See DECISIONS D13, D18, D24.
    /// </summary>
    private static Converter ZeroPaddedCode(IReadOnlyDictionary<string, object?> args, ConverterContext ctx)
    {
        var reference = ctx.References[Required<string>(args, "reference")];
        var maxLen = (int)Required<long>(args, "max_len");
        var stripDot = Arg(args, "strip_dot", true);
        var onAmbiguous = Arg(args, "on_ambiguous", "withhold");
        if (onAmbiguous is not ("withhold" or "assume_received"))
            throw new ArgumentException($"on_ambiguous must be withhold|assume_received, got '{onAmbiguous}'");
        return v =>
        {
            var raw = Str(v).Trim();
            var s = (stripDot ? raw.Replace(".", "") : raw).ToUpperInvariant();
            if (s.Length == 0) throw new ConversionException($"empty code: {Repr(v)}");
            if (!IsAllDigits(s) || s[0] == '0' || s.Length >= maxLen)
                return new CodeResult(s, raw, reference.Contains(s) ? "exact" : "not_in_reference");
            var valid = Enumerable.Range(s.Length, maxLen - s.Length + 1)
                .Select(n => s.PadLeft(n, '0')).Where(reference.Contains).ToList();
            return valid.Count switch
            {
                1 => new CodeResult(valid[0], raw, valid[0] == s ? "exact" : "repaired"),
                > 1 when onAmbiguous == "assume_received" && valid.Contains(s) => new CodeResult(s, raw, "assumed"),
                > 1 => new CodeResult(null, raw, "ambiguous"),
                _ => new CodeResult(null, raw, "unresolved"),
            };
        };
    }
}
