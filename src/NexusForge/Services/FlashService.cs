using Microsoft.Extensions.Logging;
using NexusForge.Helpers;
using NexusForge.Models;

namespace NexusForge.Services;

public class FlashService
{
    private readonly NativeJtagService _nativeJtag;
    private readonly LogService _logService;
    private readonly ILogger<FlashService> _logger;

    public FlashService(
        NativeJtagService nativeJtag,
        LogService logService,
        ILogger<FlashService> logger)
    {
        _nativeJtag = nativeJtag;
        _logService = logService;
        _logger = logger;
    }

    // v1.1.25: dropped the ghost `bool verify` parameter. It was passed through
    // by the ViewModel from a UI checkbox and then never read - jtagspi_program
    // always runs its internal verify_bank step as part of write on this bridge.
    // See the block comment in NativeJtagService.FlashSpiWithProgress.
    public Task<FlashResult> FlashAsync(
        string binFilePath,
        IProgress<FlashProgress>? progress,
        CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            var firmware = FileValidator.Validate(binFilePath);
            if (!firmware.IsValid)
            {
                _logService.Error($"Invalid firmware file: {firmware.ValidationMessage}");
                return new FlashResult
                {
                    Success = false,
                    ErrorMessage = firmware.ValidationMessage
                };
            }

            _logService.Info($"Firmware file validated: {firmware.FileName} ({firmware.FileSize / 1024}KB)");

            return _nativeJtag.FlashFirmware(binFilePath, progress, cancellationToken);
        }, cancellationToken);
    }
}
