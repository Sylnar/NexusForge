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
                _logService.Error("FTDI D2XX driver (ftd2xx.dll) is not installed.");
                _logService.Warn("Windows should auto-install FTDI drivers when you plug in the board.");
                _logService.Warn("If not, download from: https://ftdichip.com/drivers/d2xx-drivers/");
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
