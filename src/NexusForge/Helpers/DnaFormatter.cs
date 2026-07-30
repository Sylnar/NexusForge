namespace Sylnar.Helpers;

public static class DnaFormatter
{
    public static string Format(string rawDna)
    {
        if (string.IsNullOrWhiteSpace(rawDna))
            return string.Empty;

        var clean = rawDna.TrimStart('0', 'x').TrimStart('0', 'X');
        if (clean.Length == 0)
            return "0x0";

        clean = clean.PadLeft(15, '0');
        return $"0x{clean.ToUpper()}";
    }

    public static bool IsValidDna(string dna)
    {
        if (string.IsNullOrWhiteSpace(dna))
            return false;

        var clean = dna.TrimStart('0', 'x').TrimStart('0', 'X');
        return clean.Length > 0 && clean.Length <= 15 && clean.All(c => char.IsAsciiHexDigit(c));
    }
}
