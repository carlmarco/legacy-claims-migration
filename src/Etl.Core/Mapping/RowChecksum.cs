using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace Etl.Core.Mapping;

/// <summary>
/// Order-independent checksum of transformed rows, for parity with the Python engine (`etl transform --checksum`).
/// Each row is encoded as entity + typed values (type tag + canonical text, so NULL can't collide with any string
/// and 1200.00 == 1200), hashed with MD5, and the first 64 bits are summed. Equal sums per entity mean every
/// value matched, regardless of row order. The encoding must stay byte-identical to src/etl/cli.py.
/// </summary>
public sealed class RowChecksum
{
    private readonly Dictionary<string, (long Count, UInt128 Sum)> _byEntity = new();

    public static string Encode(object? v) => v switch
    {
        null => "N",
        bool b => b ? "B1" : "B0",
        long l => "I" + l.ToString(CultureInfo.InvariantCulture),
        decimal d => "D" + (d == 0 ? "0" : d.ToString("0.############################", CultureInfo.InvariantCulture)),
        DateOnly d => "T" + d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        string s => "S" + s,
        _ => throw new ArgumentException($"no canonical encoding for {v.GetType().Name}"),
    };

    public void Add(string entity, object?[] values)
    {
        var text = entity + "\x1f" + string.Join("\x1f", values.Select(Encode));
        var hash = MD5.HashData(Encoding.UTF8.GetBytes(text));
        var first64 = System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(hash);
        _byEntity[entity] = _byEntity.TryGetValue(entity, out var cur) ? (cur.Count + 1, cur.Sum + first64) : (1, first64);
    }

    public IReadOnlyDictionary<string, (long Count, string Sum)> Result() =>
        _byEntity.OrderBy(kv => kv.Key, StringComparer.Ordinal)
                 .ToDictionary(kv => kv.Key, kv => (kv.Value.Count, ((BigInteger)kv.Value.Sum).ToString(CultureInfo.InvariantCulture)));
}
