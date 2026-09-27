namespace Sylnar.ViewModels;

/// <summary>One memory BAR of a host PCIe device, as a row in the PCIe list.</summary>
public sealed record PciBarRow(string Name, string VenDev, ulong Start, ulong End, string SizeText)
{
    public string Range => $"{Start:X8}–{End:X8}";
    public string Details => $"{Name}\nVEN:DEV {VenDev}\n0x{Start:X}–0x{End:X} ({SizeText})";
}

/// <summary>16 bytes of a hex dump: "+10" and "4D 5A 90 00 …".</summary>
public sealed record HexRow(string Offset, string Bytes);

/// <summary>One little-endian DWORD of the last read.</summary>
public sealed record DwordCell(string Hex, string Details);
