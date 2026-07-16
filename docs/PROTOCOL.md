# DMM-8062A / CH9329 USB read protocol

Reverse-engineered and verified working (reads standalone, no vendor software). The meter's USB protocol is not publicly documented; this was confirmed against TECPEL's official DMM-8062A C# sample code and the WCH CH9329 datasheet.

## Device

- **Meter:** TECPEL DMM-8062A, 6000-count True RMS. The optional **USB communication module** plugs into the access port at the **back** of the meter; a USB cable connects the module to the PC. The module appears to be powered by the meter, so it disappears from USB when the meter powers off.
- **Chip:** WCH **CH9329**, `VID 0x1A86` / `PID 0xE429`. Enumerates as a **vendor HID** collection (`usage_page 0xFFA0`), 64-byte input/output/feature reports, no report ID. Windows product string: `WCH UART TO KB-MS`. The chip is in **Mode 3 (custom-HID)** — a transparent pipe to the meter's UART.

## It is request/response, not streaming

Nothing arrives until you send a read command; then the meter replies with one frame. Refresh rate is ~2–3 readings/sec. (A purely passive read returns zero bytes — this is the #1 thing that misleads people.)

## Read command

Write this 12-byte report to the device (the vendor sample builds it as `ReadData(CMDByte(0x5e))`):

```
57 AB 00 87 06 AB CD 01 5E 01 D7 04
```

How it is built:

- inner meter command `AB CD 01 5E` + 2-byte checksum → `AB CD 01 5E 01 D7`
  (checksum = sum of the 4 bytes; append `sum>>8` then `sum&0xFF`)
- CH9329 read wrapper: `57 AB 00 87` + `[len = 06]` + inner + `[sum>>8 = 04]`
  (`0x87` = CH9329 "read custom-HID data")

## Response frame

The input report looks like:

```
[reportId=00] [len=13] [AB CD] [func] [...] [03] [checksum]   … then stale buffer padding
```

- Honor the `len` byte (`0x13` = 19); everything after the frame is uninitialized padding — ignore it.
- **Value = 7 ASCII characters starting 5 bytes after `0xAB`.** Find `AB CD`, take `bytes[i+5 .. i+12]`, strip spaces. Space-padded, may carry a leading `-`. e.g. `"  120.4"` → `120.4`. The decimal point is already placed correctly.
- `func` = the byte right after `AB CD` (ACV = `0x10`; other rotary positions are not yet mapped — the ASCII value alone already has the correct magnitude).

## Two fatal gotchas

1. **Write via the interrupt-OUT endpoint as an overlapped `WriteFile`.**
   - C#: HidLibrary `WriteReport` works.
   - Python: `ctypes` → `CreateFile(..., FILE_FLAG_OVERLAPPED)` → `WriteFile` → `WaitForSingleObject` → `GetOverlappedResult`.
   - **`hidapi`'s `write()` fails** (`ERROR_IO_PENDING 0x3E5`). **`HidD_SetOutputReport` (control transfer) returns success but the meter never responds** — it doesn't reach the CH9329 command processor.
2. **The write only completes while the meter's boxed `S` symbol is on.** `S` = USB data transmission enabled (module seated + `Hz%/USB` long-press ~2s). With `S` off, the OUT endpoint never drains and the write times out (`WAIT_TIMEOUT 258`). Auto-power-off is 15 min. If reads suddenly stop, check the LCD `S`, not the code.

Reading the response is easy: HID **input reports are broadcast to all open handles**, so a plain HID read works (you can even capture alongside the vendor software).

**Multi-module / multi-PC:** identify a specific module by its USB **serial number** (`hid` enumerate `serial_number`).

## How it was cracked (reusable for similar WCH-HID instruments)

1. Identify the chip from VID/PID → WCH CH9329.
2. Enumerate HID collections → single vendor `0xFFA0`, 64-byte reports.
3. Passive capture → 0 reports ⇒ request/response, not streaming.
4. Capture HID input **alongside the running vendor software** (broadcast) → decode the ASCII value in the frame.
5. Disconnect the vendor app → data stops ⇒ the app sends a start/poll command.
6. Get the authoritative command + frame from the vendor **sample code** (don't brute-force).
7. Solve the write channel: hidapi write fails → `HidD_SetOutputReport` gives no reply → **overlapped `WriteFile`** works, but only while `S` is on.

## References

- WCH CH9329 datasheet — `https://www.wch-ic.com/products/CH9329.html` (Mode 3 custom-HID; `57 AB …` command frames).
- TECPEL DMM-8062A user manual (USB Data Transmission section; the boxed `S` symbol).
- TECPEL's official DMM-8062A C# sample (the `DMM8062A` class: `ReadData` / `CMDByte` / `Decipher`) — the ground-truth implementation this repo mirrors.
