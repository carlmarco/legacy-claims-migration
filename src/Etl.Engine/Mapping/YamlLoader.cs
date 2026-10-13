using System.Globalization;
using System.Text.RegularExpressions;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Etl.Core.Mapping;

/// <summary>
/// Loads YAML into plain .NET values with PyYAML's scalar typing, so the same spec means the same thing
/// to both engines: plain true/false/yes/no/on/off -> bool, integers -> long, floats -> double,
/// ~/null/empty -> null; quoted scalars stay strings. Mappings keep their key order (column order matters).
/// Merge keys (`<<: *anchor`) are resolved via MergingParser; without it YamlDotNet would keep "<<" as a key.
/// </summary>
public static partial class YamlLoader
{
    public static object? LoadFile(string path)
    {
        using var reader = new StreamReader(path);
        return Load(reader);
    }

    public static object? Load(TextReader reader)
    {
        var stream = new YamlStream();
        stream.Load(new MergingParser(new Parser(reader)));
        return stream.Documents.Count == 0 ? null : ToPlain(stream.Documents[0].RootNode);
    }

    private static object? ToPlain(YamlNode node) => node switch
    {
        YamlMappingNode m => ToMap(m),
        YamlSequenceNode s => s.Children.Select(ToPlain).ToList(),
        YamlScalarNode s => Scalar(s),
        _ => throw new InvalidDataException($"unsupported YAML node {node.NodeType}"),
    };

    private static OrderedDictionary<string, object?> ToMap(YamlMappingNode m)
    {
        var map = new OrderedDictionary<string, object?>();
        foreach (var (k, v) in m.Children)
            map[((YamlScalarNode)k).Value ?? ""] = ToPlain(v);
        return map;
    }

    [GeneratedRegex(@"^[-+]?(0|[1-9][0-9_]*)$")]
    private static partial Regex IntPattern();

    [GeneratedRegex(@"^[-+]?([0-9][0-9_]*)?\.[0-9_]*([eE][-+][0-9]+)?$")]
    private static partial Regex FloatPattern();

    private static object? Scalar(YamlScalarNode s)
    {
        var v = s.Value ?? "";
        if (s.Style is not (ScalarStyle.Plain or ScalarStyle.Any)) return v;   // quoted: always a string
        switch (v)
        {
            case "" or "~" or "null" or "Null" or "NULL": return null;
            case "true" or "True" or "TRUE" or "yes" or "Yes" or "YES" or "on" or "On" or "ON": return true;
            case "false" or "False" or "FALSE" or "no" or "No" or "NO" or "off" or "Off" or "OFF": return false;
        }
        if (IntPattern().IsMatch(v)) return long.Parse(v.Replace("_", ""), CultureInfo.InvariantCulture);
        if (FloatPattern().IsMatch(v) && v != ".") return double.Parse(v.Replace("_", ""), CultureInfo.InvariantCulture);
        return v;
    }
}
