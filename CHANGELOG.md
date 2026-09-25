# Changelog

## v1.1.28 — 2026-09-25

### Important: updating from v1.1.27 or older
Versions up to v1.1.27 check `NexusForge-dev/NexusForge` for updates, not
`Sylnar/NexusForge`, so they will not see this release. Download v1.1.28
once from the releases page; from then on auto-update uses the Sylnar repo.

### New
- **Redesigned interface**
  - The window is now resizable, from 900×680 up; every tab scrolls.
  - Proper hover and click colours on buttons, and visible keyboard focus.
  - Teal accent across tabs, checkboxes, text selection and input borders.
  - Refreshed header with a gradient logo and accent line; soft card shadows.
- **Flash result banner:** a green or red banner shows whether the flash
  worked. For `.bin` files it says to power-cycle the target PC. For `.bit`
  files it says the bitstream is active now and lost at power-off.
- **Remembers your firmware file:** the Flash tab reopens the last file you
  picked.
- **Safer auto-update:** the downloaded update is checked against the SHA-256
  hash GitHub publishes before it replaces the app.

### Fixed
- **Flashing**
  - The Cancel button was never enabled during a flash. Cancel now works until
    the SPI write starts. After that the write finishes, because stopping it
    halfway would leave the board unbootable.
  - Flash progress could jump or be wrong: the two output streams were parsed
    at the same time without locking.
  - Firmware paths containing `{`, `}` or `"` broke the flash command. They now
    get a clear message.
  - Board info showed the XC7A75T package for every FPGA.
- **Drivers**
  - The Drivers and DMA tabs could crash the app when a driver check timed
    out or an install failed.
  - A driver check that failed was reported as "driver not installed". It now
    says "Check failed".
  - "Restart required" and real install failures now have their own messages,
    instead of "may already exist" and "Install in browser".
- **DMA testing**
  - Speed test, mmap generation and BAR Probe could run at the same time and
    fail with a misleading "check your card" error. A second operation now
    shows "Another DMA operation is already running".
  - A saved memory map used hundreds of MB of RAM on 32–64 GB target PCs.
    It is now sampled to at most 50,000 pages.
  - BAR Probe kept re-reading its log file forever after a poll stopped on
    its own.
- **Cleanup and stability**
  - Temp-file cleanup deleted every `%TEMP%\nf_*` and `drv_*` folder,
    including other programs' folders and a second open Sylnar's tools. It now
    deletes only folders this app created.
  - Update and cleanup scripts failed for Windows usernames with accented or
    non-Latin characters.
  - The status log was not thread-safe and grew without limit. It now keeps
    the last 5,000 entries.
  - Windows hardware-query handles and log-panel event handlers leaked over
    time.
- **Wording:** outdated text on the Drivers tab, in board detection and in
  the README.

### Other
- XC7A15T flashing is marked experimental. It uses the XC7A35T SPI bridge,
  and the app now warns about this.
- Releases are built by GitHub Actions (`.github/workflows/build.yml`).
  To release: bump `<Version>` in `NexusForge.csproj`, push, then run the
  **Build** workflow with "release" ticked.
- Removed the unused `appsettings.json` and dead theme code.
