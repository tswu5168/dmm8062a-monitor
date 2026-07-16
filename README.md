# DMM-8062A Web

Read a **TECPEL DMM-8062A** digital multimeter over USB and show its live value + connection info on a web page — one small agent per PC, no cloud, no drivers to write.

The USB communication module uses a **WCH CH9329** HID chip (`VID 1A86` / `PID E429`). This repo contains a reverse-engineered, verified-working reader (the meter's protocol is *not* publicly documented), a self-hosted web UI, and a standalone Python reference implementation.

> Origin: built to auto-fill measurement fields in an internal manufacturing portal. It's fully self-contained and has no ties to that system, so it lives here as an independent tool.

---

## What it does

A local **agent** (`dmm-meter-agent`, .NET 8) opens the meter, polls it, and serves a small JSON API + a monitor web page on `http://localhost:8765/`:

- **即時數值 / live value** — big, centered, updates ~2×/sec, with a 600-second time-axis trend.
- **連線狀態 / connection status** — agent, USB module, meter (the boxed `S` symbol).
- **連線資訊 / connection info** — Windows Device Manager name & instance ID, chip product string, VID/PID, poll interval, page start time.

The web page is embedded in the agent, so on each PC you just run the agent and open `http://localhost:8765/`.

## Why a local agent (and not a hosted web page)

The meter is a **local USB device**. A page hosted on a public `https://` origin **cannot** read `http://localhost` (browser Mixed-Content + Private Network Access protections), and a browser cannot perform the interrupt-OUT overlapped write this chip needs. So the reader must run locally. The agent serves the page same-origin on `localhost`, which sidesteps every cross-origin issue.

```
meter ──USB──▶ dmm-meter-agent (per PC) ──▶ http://localhost:8765/         (monitor web page)
                                        └──▶ http://localhost:8765/reading  (JSON + CORS *)
```

## Repository layout

```
agent/                     .NET 8 local agent + embedded web UI
  Program.cs               agent: HID read + HttpListener + embedded page
  dmm-meter-agent.csproj
  meter-web-form.html      the monitor page (embedded into the exe)
python/
  dmm_reader.py            standalone reference reader (ctypes + hidapi)
docs/
  PROTOCOL.md              the reverse-engineered DMM-8062A / CH9329 protocol
```

## Requirements

- Windows (uses Win32 HID + WMI).
- .NET 8 SDK to build (`net8.0-windows`).
- A DMM-8062A with its **USB communication module** seated in the meter's back port.
- On the meter: press **`Hz%/USB`** so the LCD **boxed `S`** shows (USB data transmission on). Data only flows while `S` is on.

## Quick start

```powershell
# build
dotnet build .\agent\dmm-meter-agent.csproj -c Release

# run
dotnet run --project .\agent\dmm-meter-agent.csproj
#   or the built exe:  .\agent\bin\Release\net8.0-windows\dmm-meter-agent.exe

# then open the monitor page
start http://localhost:8765/
```

Options: `--port <n>` (default 8765) · `--poll <ms>` (default 500) · `--serial <usb-module-serial>` (pick a specific module when several are attached).

## Deploy to many PCs

Publish a self-contained single exe (no .NET runtime needed on target):

```powershell
dotnet publish .\agent\dmm-meter-agent.csproj -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true -o .\publish
```

Copy the exe to each PC, set it to auto-start, and open `http://localhost:8765/`. Use `--serial` if a PC has more than one module.

## HTTP API

`GET /reading` (also `/api/reading`, `/info`) — CORS `Access-Control-Allow-Origin: *`:

```json
{
  "agent":  { "version": "1.0", "port": 8765, "pollMs": 500, "vid": "0x1A86", "pid": "0xE429" },
  "device": { "present": true, "vid": "0x1A86", "pid": "0xE429", "serial": "…",
              "product": "WCH UART TO KB-MS_V1.7",
              "name": "符合 HID 標準的廠商定義裝置",
              "instanceId": "HID\\VID_1A86&PID_E429\\6&…" },
  "meter":  { "connected": true, "value": "120.4", "numeric": 120.4, "note": null, "ts": "…" }
}
```

`meter.connected` is `false` (with a `note`) when the boxed `S` is off or the module isn't detected. Any web app can `fetch` this from `localhost` to display or auto-fill the reading.

## Python reference reader

`python/dmm_reader.py` is a dependency-light standalone reader (`hidapi` + `ctypes`) — handy for scripting or porting to other stacks. It documents and implements the exact write/read sequence.

## Protocol

The meter is **request/response** over CH9329 custom-HID — you send a read command and get one reply; it does not free-stream. Two non-obvious gotchas make it tricky:

1. The write **must** use the interrupt-OUT endpoint via an overlapped `WriteFile` (C#: HidLibrary `WriteReport`; Python: `ctypes` `WriteFile`). `hidapi`'s `write()` and `HidD_SetOutputReport` do **not** work.
2. The write only completes while the meter's **boxed `S`** is on.

Full details — command bytes, frame format, value parsing, and how it was reverse-engineered — are in **[docs/PROTOCOL.md](docs/PROTOCOL.md)**.

## Credits & license

Protocol confirmed against TECPEL's official DMM-8062A C# sample code and the CH9329 datasheet. Licensed under the [MIT License](LICENSE).
