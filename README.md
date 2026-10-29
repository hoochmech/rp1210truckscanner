# RP1210 Truck Scanner

A Windows app that uses whatever RP1210-compliant adapter is installed on the customer's PC
(Nexiq USB-Link, Noregon DLA+, DG Technologies DPA, Cummins INLINE, etc.) to identify a truck:

- **VIN**, truck make and model year
- **Engine make, model and serial**
- **ECM type, part number and software/calibration ID**
- Every other module that answers (transmission, ABS, ...)

## How it works

```
 RP1210.ini  ──►  Rp1210Ini        finds installed drivers + devices + protocols
 VENDOR.dll  ──►  Rp1210Api        late-bound P/Invoke (LoadLibrary/GetProcAddress)
                  Rp1210J1939Bus   J1939 client, claims address 0xF9 (service tool)
                  Rp1210J1708Bus   J1708 client for older trucks
                  TruckScanner     sends requests, collects replies, builds ScanResult
                  VinDecoder       WMI → make, year code, check digit
```

### J1939 (2007+ trucks, most data)

The scanner sends a Request (PGN 59904) to global and then directly to the engine for:

| PGN | Name | Gives |
|-----|------|-------|
| 60928 | Address Claimed | Module NAME, J1939 manufacturer code |
| 65260 | Vehicle Identification | VIN |
| 65259 | Component Identification | Make, model, serial, unit number |
| 64965 | ECU Identification | ECU part number, serial, **type** (e.g. CM2350), manufacturer, hardware ID |
| 65242 | Software Identification | Calibration / software IDs |

The RP1210 driver reassembles multi-packet (BAM and RTS/CTS) replies, so the app receives whole messages.

### J1587 fallback (older trucks)

If the engine doesn't give component data on J1939, the scanner opens J1708 and requests
PID 237 (VIN), PID 243 (component ID) and PID 234 (software ID) using PID 0 from MID 172.

## Build

Requires the .NET 8 SDK on Windows.

```
dotnet test
dotnet publish src/Rp1210Scanner.App -c Release
```

The app is built **x86** because most vendor RP1210 DLLs are 32-bit and a 64-bit process can't load them.
The published output is a single self-contained `TruckScanner.exe` (no .NET install needed on the customer PC).

Pick **Simulated truck** in the adapter list to try the UI without hardware.

## Status / known gaps

- Not yet tested against real adapters. The RP1210 byte layouts follow RP1210C; some older RP1210A drivers
  differ in protocol strings (handled with fallbacks) and in echo/timestamp behavior.
- Engine model from J1939 depends on what the OEM puts in Component ID. Some engines leave it blank;
  a future step is a lookup from ECU part number / software ID to engine family.
- VIN WMI table covers the main North American heavy-truck makers only.
- 64-bit RP1210 drivers (RP1210 64-bit DLLs) would need an x64 build.

## Releases

Push a tag like `v0.2.0`, or run the `release` workflow from the Actions tab with a version,
and it runs the tests, builds `TruckScanner.exe` (stamped with that version), creates the tag
if needed, and publishes it on the GitHub Releases page.
