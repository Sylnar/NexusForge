using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;

namespace Sylnar.Helpers;

internal static class ResourceCrypto
{
    private static readonly byte[] _kS1 = { 0x4E,0x65,0x78,0x75,0x73,0x46,0x6F,0x72 };
    private static readonly byte[] _kS2 = { 0x67,0x65,0x2D,0x46,0x50,0x47,0x41,0x2D };
    private static readonly byte[] _kS3 = { 0x44,0x4D,0x41,0x2D,0x32,0x30,0x32,0x36 };
    private static readonly byte[] _kS4 = { 0x2D,0x41,0x45,0x53,0x2D,0x4B,0x45,0x59 };

    private static readonly byte[] _iS1 = { 0x4E,0x65,0x78,0x75,0x73,0x46,0x6F,0x72 };
    private static readonly byte[] _iS2 = { 0x67,0x65,0x2D,0x41,0x45,0x53,0x2D,0x49 };
    private static readonly byte[] _iS3 = { 0x56,0x2D,0x32,0x30,0x32,0x36 };

    private static byte[] DeriveKey()
    {
        var s = new byte[_kS1.Length + _kS2.Length + _kS3.Length + _kS4.Length];
        _kS1.CopyTo(s, 0);
        _kS2.CopyTo(s, _kS1.Length);
        _kS3.CopyTo(s, _kS1.Length + _kS2.Length);
        _kS4.CopyTo(s, _kS1.Length + _kS2.Length + _kS3.Length);
        return SHA256.HashData(s);
    }

    private static byte[] DeriveIV()
    {
        var s = new byte[_iS1.Length + _iS2.Length + _iS3.Length];
        _iS1.CopyTo(s, 0);
        _iS2.CopyTo(s, _iS1.Length);
        _iS3.CopyTo(s, _iS1.Length + _iS2.Length);
        return MD5.HashData(s);
    }

    public static bool ExtractResource(Assembly assembly, string fileName, string destPath)
    {
        var resourceName = assembly.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith(fileName, StringComparison.OrdinalIgnoreCase));
        if (resourceName == null) return false;

        using var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream == null) return false;

        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        var encrypted = ms.ToArray();

        using var aes = Aes.Create();
        aes.Key = DeriveKey();
        aes.IV  = DeriveIV();
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;

        using var decryptor = aes.CreateDecryptor();
        var compressed = decryptor.TransformFinalBlock(encrypted, 0, encrypted.Length);

        using var gzIn = new GZipStream(new MemoryStream(compressed), CompressionMode.Decompress);
        using var outFs = File.Create(destPath);
        gzIn.CopyTo(outFs);

        return true;
    }
}
