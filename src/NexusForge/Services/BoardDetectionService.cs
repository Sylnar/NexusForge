using Microsoft.Extensions.Logging;
using Sylnar.Models;

namespace Sylnar.Services;

public class BoardDetectionService
{
    private readonly NativeJtagService _nativeJtag;
    private readonly LogService _logService;
    private readonly ILogger<BoardDetectionService> _logger;

    public BoardDetectionService(
        NativeJtagService nativeJtag,
        LogService logService,
        ILogger<BoardDetectionService> logger)
    {
        _nativeJtag = nativeJtag;
        _logService = logService;
        _logger = logger;
    }

    public Task<BoardInfo> DetectBoardAsync(CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            _logService.Info("Attempting board detection via native JTAG...");

            try
            {
                var boardInfo = _nativeJtag.DetectBoard();
                return boardInfo;
            }
            catch (DllNotFoundException)
            {
                // Detection runs through the bundled OpenOCD + CH347, not FTDI D2XX.
                _logService.Error("A required JTAG library could not be loaded.");
                _logService.Warn("Install the CH347 driver from the Drivers tab, replug the USB cable, and retry.");
                return new BoardInfo();
            }
            catch (Exception ex)
            {
                _logService.Error($"Detection error: {ex.Message}");
                _logger.LogError(ex, "Board detection failed");
                return new BoardInfo();
            }
        }, ct);
    }
}
