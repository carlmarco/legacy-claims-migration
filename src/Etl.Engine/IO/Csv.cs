using System.Text;

namespace Etl.Core.IO;

/// <summary>Minimal RFC 4180 CSV reader/writer: quoted fields, escaped quotes, embedded commas and newlines.</summary>
public static class Csv
{
    /// <summary>Header + rows. Empty fields become null: CSV can't distinguish NULL from '', and the engine's
    /// null tokens treat both as missing anyway.</summary>
    public static (List<string> Header, List<object?[]> Rows) Read(string text)
    {
        var records = new List<List<string>>();
        var field = new StringBuilder();
        var record = new List<string>();
        var inQuotes = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (inQuotes)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                else if (c == '"') inQuotes = false;
                else field.Append(c);
                continue;
            }
            switch (c)
            {
                case '"': inQuotes = true; break;
                case ',': record.Add(field.ToString()); field.Clear(); break;
                case '\r': break;
                case '\n': record.Add(field.ToString()); field.Clear(); records.Add(record); record = []; break;
                default: field.Append(c); break;
            }
        }
        if (field.Length > 0 || record.Count > 0) { record.Add(field.ToString()); records.Add(record); }
        if (records.Count == 0) return ([], []);
        var header = records[0].Select(h => h.Trim()).ToList();
        var rows = records.Skip(1).Where(r => !(r.Count == 1 && r[0].Length == 0))
            .Select(r => Enumerable.Range(0, header.Count).Select(i => i < r.Count && r[i].Length > 0 ? (object?)r[i] : null).ToArray())
            .ToList();
        return (header, rows);
    }

    public static string Field(object? v)
    {
        var s = v switch
        {
            null => "",
            DateOnly d => d.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
            bool b => b ? "true" : "false",
            IFormattable f => f.ToString(null, System.Globalization.CultureInfo.InvariantCulture),
            _ => v.ToString() ?? "",
        };
        return s.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
    }
}
