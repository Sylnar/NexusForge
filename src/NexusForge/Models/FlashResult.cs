namespace Sylnar.Models;

public class FlashResult
{
    public bool Success { get; set; }
    public string ErrorMessage { get; set; } = string.Empty;
    public TimeSpan Duration { get; set; }
    // v1.1.25: removed `Verified` - the underlying jtagspi_program always runs
    // its internal verify step (flash verify_bank 0) as part of write, and this
    // bridge does not surface a separate read-back verify result to us. The
    // property was set but no caller ever read it. See the block comment in
    // NativeJtagService.FlashSpiWithProgress for the full history.
}
