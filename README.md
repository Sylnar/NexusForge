<p align="center">
  <img src="https://img.shields.io/badge/Sylnar-v1.1.27-00D4AA?style=for-the-badge&labelColor=0D1117" />
  <img src="https://img.shields.io/badge/Platform-Windows%2010%2F11-0078D6?style=for-the-badge&logo=windows&logoColor=white&labelColor=0D1117" />
  <img src="https://img.shields.io/badge/.NET%208-512BD4?style=for-the-badge&logo=dotnet&logoColor=white&labelColor=0D1117" />
  <img src="https://img.shields.io/badge/License-Proprietary-FF5252?style=for-the-badge&labelColor=0D1117" />
</p>

<h1 align="center">Sylnar</h1>

<p align="center">
  <strong>Professional FPGA DMA Board Management Tool</strong><br/>
  <sub>Detect boards, read DNA, flash firmware, install drivers, test DMA — all in one app.</sub>
</p>

---

## Overview

Sylnar is a standalone Windows application for managing FPGA-based DMA boards. It provides a clean, modern interface to perform every operation needed for board setup and firmware deployment — no Vivado installation required.

**One file. No dependencies. Just run `Sylnar.exe`.**

---

## Features

| Feature | Description |
|---------|-------------|
| **Board Detection** | Auto-detect FPGA via JTAG — shows device name, IDCODE, and silicon DNA |
| **DNA Reading** | Read the unique 57-bit Device DNA from any supported Xilinx 7-series FPGA |
| **Firmware Flashing** | Flash `.bin` files to SPI flash (persistent) or `.bit` files to SRAM (volatile) |
| **Real-time Progress** | Live progress bar with sector-by-sector erase/write/verify tracking |
| **Driver Management** | One-click install for CH347 JTAG and FTDI FT601 USB3 drivers |
| **DMA Speed Test** | Test DMA read speed and verify physical memory access |
| **Auto-Update** | Checks for new versions on launch and updates automatically |

---

## Supported Boards

| FPGA | Detect | Flash | Common DMA Boards |
|------|--------|-------|-------------------|
| XC7A15T | Yes | Yes | — |
| XC7A35T | Yes | Yes | LeetDMA, CaptainDMA M2, CaptainDMA 4.1th, GBOX, Squirrel, ScreamerM2 |
| XC7A50T | Yes | Yes | — |
| XC7A75T | Yes | Yes | CaptainDMA 75T, Enigma X1 |
| XC7A100T | Yes | Yes | CaptainDMA 100T, CaptainDMA M2 100T, ZDMA |
| XC7A200T | Yes | Yes | — |
| XC7K325T | Yes | No | — |
| XC7K355T | Yes | No | — |
| XC7K410T | Yes | No | — |

> All Artix-7 boards are fully supported (detect + flash). Kintex-7 boards can be detected but SPI flashing is not available.

---

## Quick Start

### 1. Download
Download `Sylnar.exe` from the [latest release](../../releases/latest).

### 2. Install Drivers (First Time Only)
- Connect your DMA board via USB
- Launch Sylnar (run as Administrator)
- Go to the **Drivers** tab
- Click **Install CH347 Driver** (for JTAG)
- Click **Install FT601 Driver** (for DMA data)

### 3. Detect Board
- Click **Detect Board**
- Sylnar will display your board info and **Device DNA**
- Click **Copy DNA** to copy it to clipboard

### 4. Flash Firmware
- Click **Browse** and select your `.bin` firmware file
- Click **Flash Firmware**
- Wait for erase → write → verify to complete
- Power cycle the target PC

---

## How It Works

```
┌──────────────┐     USB      ┌──────────────┐     PCIe     ┌──────────────┐
│   Radar PC   │◄────────────►│  DMA Board   │◄────────────►│  Gaming PC   │
│              │   CH347 JTAG │  (FPGA)      │   x1 Gen2    │              │
│  Sylnar  │   FT601 Data │              │              │  Target      │
└──────────────┘              └──────────────┘              └──────────────┘
```

Sylnar communicates with the DMA board over USB:
- **CH347 JTAG** — Used for board detection, DNA reading, and firmware flashing via OpenOCD
- **FT601 USB3** — Used for DMA data transfer (speed testing)

---

## Customer Workflow

```
1. Install Sylnar on radar PC
2. Connect DMA board via USB
3. Click "Detect Board" → copy DNA
4. Send DNA to firmware provider
5. Receive DNA-locked firmware (.bin)
6. Click "Flash Firmware" → select .bin → flash
7. Power cycle gaming PC
8. Done — firmware is active
```

---

## Auto-Update

Sylnar checks for updates automatically when launched. If a new version is available:
1. Downloads the update in the background
2. Shows status in the log panel
3. Replaces the old exe and restarts

No manual downloads needed after initial install.

---

## System Requirements

| Requirement | Detail |
|-------------|--------|
| OS | Windows 10/11 x64 |
| Privileges | Administrator (required for driver install and JTAG access) |
| USB | USB port for DMA board connection |
| Disk | ~60 MB for the application |

---

## Troubleshooting

| Issue | Solution |
|-------|----------|
| "CH347 adapter not found" | Install the CH347 driver from the Drivers tab, then replug USB |
| "FPGA not responding" | Ensure the board has power (PCIe slot must be powered). Replug USB. |
| "Could not read DNA" | Replug USB cable. Make sure only one JTAG adapter is connected. |
| Flash fails at erase | Power cycle the board, then retry. Check USB cable quality. |
| App won't start | Run as Administrator. Check Windows Defender isn't blocking it. |

---

<p align="center">
  <sub>Sylnar — Built for the community</sub>
</p>
