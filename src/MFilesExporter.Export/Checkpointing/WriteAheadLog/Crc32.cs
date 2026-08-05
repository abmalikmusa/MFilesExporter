namespace MFilesExporter.Export.Checkpointing.WriteAheadLog;

/// <summary>
/// Reference-quality CRC-32 (IEEE 802.3 polynomial 0xEDB88320) — the same
/// polynomial used by zlib, PNG, and gzip. Used to detect torn WAL writes
/// on recovery (partial line = mismatched CRC = discard).
/// </summary>
/// <remarks>
/// Hand-rolled to keep this project dependency-free of System.IO.Hashing.
/// Cost is trivial (a few KB of state per process; a few CPU cycles per byte).
/// </remarks>
internal static class Crc32
{
    private static readonly uint[] Table = BuildTable();

    /// <summary>Computes CRC-32 over the UTF-8 bytes of <paramref name="input"/>.</summary>
    public static uint Compute(ReadOnlySpan<byte> input)
    {
        uint crc = 0xFFFFFFFF;
        foreach (var b in input)
        {
            crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }
        return crc ^ 0xFFFFFFFF;
    }

    /// <summary>Convenience: CRC-32 of a UTF-8 string, formatted as 8-char lowercase hex.</summary>
    public static string ComputeHex(string input)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(input);
        return Compute(bytes).ToString("x8", System.Globalization.CultureInfo.InvariantCulture);
    }

    private static uint[] BuildTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            var c = i;
            for (var k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            }
            table[i] = c;
        }
        return table;
    }
}
