namespace Mpm.Core.Mpq;

[Flags]
public enum MpqFlags : uint
{
    Implode = 0x00000100,
    Compress = 0x00000200,
    Encrypted = 0x00010000,
    FixKey = 0x00020000,
    SingleUnit = 0x01000000,
    DeleteMarker = 0x02000000,
    SectorCrc = 0x04000000,
    Exists = 0x80000000,
}
