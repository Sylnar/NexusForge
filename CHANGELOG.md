# Changelog

## v1.1.32 — 2026-10-18

### Changed
- **Every SPI flash now leaves a complete record.** Sylnar used to keep only its
  own short summary of a flash and throw away everything the programmer printed,
  so when an erase went wrong there was nothing to look at afterwards. Each flash
  now writes a timestamped record to `%LOCALAPPDATA%\Sylnar\flash\`, and the log
  names the file at the start of the flash. The newest 20 are kept. The record is
  written as it happens, so it survives closing the window part-way through.
- **The log now says how the erase went:** whether the chip was erased sector by
  sector or as a whole, how long it took, and how long after the flash chip was
  detected the writing began.
- **A too-fast erase is flagged.** If the erase finishes faster than a real flash
  erase can, Sylnar warns that the flash may not have been erased before writing.

Nothing about what is written to the board changed in this release.

## v1.1.31 — 2026-10-18

### Changed
- **The startup and shutdown screens now look like the rest of the app.** They had
  been designed separately and had drifted: a different Sylnar logo, with a
  gradient fill and a teal glow instead of the flat tile the app shows in its
  sidebar, on rounder panels than any surface in the app. The logo is now the same
  mark everywhere, scaled rather than redrawn, and both screens use the app's own
  colours, type and progress bars.
- The shutdown screen now carries the Sylnar mark and says what it is doing, rather
  than only "Please wait".

### Fixed
- **A flash that failed could blame the wrong thing.** Some chips refuse the fast
  whole-chip erase and fall back to erasing sector by sector. If a flash took that
  path and then failed for any unrelated reason, Sylnar read the fallback notice as
  a failure and reported the chip as write-protected, advising an external
  programmer and a chip clip that were never needed. Genuine write protection is
  still reported.
- The shutdown screen had square corners where every other window is rounded, and
  put a second Sylnar button in the taskbar for as long as it was open.
- A failure while the startup screen was on its way out could leave the app running
  with no window at all. The startup screen now always hands over.

Nothing about what is written to the board changed in this release.

## v1.1.30 — 2026-10-18

### Fixed
- **The Erase step now appears when it should.** Pressing Flash often went
  straight to Write with no Erase step, and only sometimes showed the erase
  after a few restarts. The board was always being erased — the app simply
  could not see it happening. Most SPI chips erase in one whole-chip command
  that reports nothing while it runs, and the only line the app watched for is
  one the chip prints solely on the slower sector-by-sector fallback. The erase
  is now shown for both, with per-sector progress where the chip reports it.
- A chip that refuses the fast whole-chip erase and falls back to erasing
  sector by sector now says so in the log, which is also why those runs take
  noticeably longer.
- The step bar, the percentage and the status line no longer jump backwards.
  Progress arrives on two separate streams that are read independently, so a
  late line could previously drag the display back a step.

Nothing about what is written to the board changed in this release.

## v1.1.29 — 2026-09-27

### New
- **Compact redesign** (1100×680 by default, resizable down to 1000×560)
  - Sidebar navigation replaces the tabs: Board & Flash, Drivers, DMA Test,
    BAR Probe. Its Hardware card shows the CH347 and FT601 driver status,
    checked in the background at launch, and the footer shows the update
    check result and whether Sylnar runs as Administrator.
  - The log is a dock under every page, with All / Warnings / Errors filters,
    a line count, Copy (copies what the filter shows) and a collapse button.
  - New type: IBM Plex Sans for the interface, JetBrains Mono for IDs,
    addresses and the log (both SIL OFL, bundled in `Assets/Fonts`).
- **Board & Flash:** board and firmware side by side. The board card shows
  the part, family, detection time, IDCODE, package, cable and the BSCAN
  bridge used for flashing; the DNA is grouped in fours and Copy confirms
  with "Copied". The firmware card shows the flash mode (SPI flash or SRAM),
  a Bridge · Erase · Write · Done step bar, the live percentage and the
  elapsed time. A `.bin` or `.bit` file can be dropped on the card.
- **Drivers:** CH347 and FT601 drivers side by side (the FT601 driver moved
  here from the DMA tab), each with its device, VID/PID, version, driver,
  a plain-language status and Check / Install / Reinstall / Uninstall.
  "Check both" checks the two at once.
- **DMA Test:** test type is a segmented control next to Run test. Results
  show a coloured rating banner, six metric tiles (labelled for the stress
  test's own figures) and the rating scale with the reached level lit.
  The memory-map card shows the cache age and where the map is saved.
- **BAR Probe:** the PCIe device list loads when the page is first opened;
  clicking a row copies its BAR start into Address. Last read shows the hex
  dump with offsets, DWORDs as tiles, or the decoded config header.

### Changed
- Cancel is only enabled while it can still take effect (before the SPI
  erase starts), and says so next to the button.
- Splash, update and shutdown windows use the new colours; the update
  window's logo letter is now "S".

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
