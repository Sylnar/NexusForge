using Sylnar.Models;

namespace Sylnar.Helpers;

public static class FileValidator
{
    private const long MinFileSize = 1024;
    private const long MaxFileSize = 16 * 1024 * 1024;

    public static FirmwareFile Validate(string filePath)
    {
        var firmware = new FirmwareFile
        {
            FilePath = filePath,
            FileName = Path.GetFileName(filePath)
        };

        if (string.IsNullOrWhiteSpace(filePath))
        {
            firmware.ValidationMessage = "No file path provided";
            firmware.IsValid = false;
            return firmware;
        }

        if (!File.Exists(filePath))
        {
            firmware.ValidationMessage = "File does not exist";
            firmware.IsValid = false;
            return firmware;
        }

        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        if (ext != ".bin" && ext != ".bit")
        {
            firmware.ValidationMessage = "File must be a .bin or .bit file";
            firmware.IsValid = false;
            return firmware;
        }

        var fileInfo = new FileInfo(filePath);
        firmware.FileSize = fileInfo.Length;

        if (firmware.FileSize < MinFileSize)
        {
            firmware.ValidationMessage = $"File too small ({firmware.FileSize} bytes). Must be at least 1KB.";
            firmware.IsValid = false;
            return firmware;
        }

        if (firmware.FileSize > MaxFileSize)
        {
            firmware.ValidationMessage = $"File too large ({firmware.FileSize / (1024 * 1024)}MB). Must be under 16MB.";
            firmware.IsValid = false;
            return firmware;
        }

        firmware.IsValid = true;
        firmware.ValidationMessage = $"Valid firmware ({firmware.FileSize / 1024}KB)";
        return firmware;
    }
}
