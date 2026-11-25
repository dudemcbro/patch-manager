using System.Buffers.Binary;

namespace Mpm.Core.Mpq;

/// <summary>MPQ hashing and table/file encryption (Blizzard's crypt table scheme).</summary>
internal static class MpqCrypto
{
    public const uint HashOffset = 0x000;
    public const uint HashA = 0x100;
    public const uint HashB = 0x200;
    public const uint HashFileKey = 0x300;

    private static readonly uint[] Table = BuildTable();

    private static uint[] BuildTable()
    {
        var table = new uint[0x500];
        uint seed = 0x00100001;
        for (var i = 0; i < 0x100; i++)
        {
            for (var j = 0; j < 5; j++)
            {
                seed = (seed * 125 + 3) % 0x2AAAAB;
                var hi = (seed & 0xFFFF) << 16;
                seed = (seed * 125 + 3) % 0x2AAAAB;
                table[i + j * 0x100] = hi | (seed & 0xFFFF);
            }
        }
        return table;
    }

    public static uint HashString(string name, uint hashType)
    {
        uint s1 = 0x7FED7FED, s2 = 0xEEEEEEEE;
        foreach (var c in name)
        {
            uint ch = char.ToUpperInvariant(c == '/' ? '\\' : c);
            s1 = Table[hashType + ch] ^ (s1 + s2);
            s2 = ch + s1 + s2 + (s2 << 5) + 3;
        }
        return s1;
    }

    /// <summary>Decrypts whole uint32 words in place; a trailing partial word is left as-is.</summary>
    public static void Decrypt(Span<byte> data, uint key) => Crypt(data, key, decrypting: true);

    public static void Encrypt(Span<byte> data, uint key) => Crypt(data, key, decrypting: false);

    private static void Crypt(Span<byte> data, uint key, bool decrypting)
    {
        uint s2 = 0xEEEEEEEE;
        for (var i = 0; i + 4 <= data.Length; i += 4)
        {
            s2 += Table[0x400 + (key & 0xFF)];
            var value = BinaryPrimitives.ReadUInt32LittleEndian(data[i..]);
            var result = value ^ (key + s2);
            BinaryPrimitives.WriteUInt32LittleEndian(data[i..], result);
            var plain = decrypting ? result : value;
            key = ((~key << 0x15) + 0x11111111) | (key >> 0x0B);
            s2 = plain + s2 + (s2 << 5) + 3;
        }
    }

    public static uint FileKey(string name, uint blockOffset, uint fileSize, bool fixKey)
    {
        var normalized = name.Replace('/', '\\');
        var key = HashString(normalized[(normalized.LastIndexOf('\\') + 1)..], HashFileKey);
        return fixKey ? (key + blockOffset) ^ fileSize : key;
    }
}
