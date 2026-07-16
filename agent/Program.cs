// =============================================================================
// SCMC DMM Meter Agent — 每台電腦本機讀 TECPEL DMM-8062A(CH9329 HID) 數位電表，
// 並在 http://localhost:<port>/reading 以 JSON(+CORS) 提供即時讀值 + 連線資訊，
// 供 SCMC Web 前端或獨立 web form fetch 後顯示 / 自動填入量測欄位。
//
// 通訊協定取自原廠 DMM-8062A 範例碼(Form1.cs DMM8062A 類別)，已驗證可獨立讀值：
//   讀值指令 0x5e -> 寫 57 AB 00 87 06 AB CD 01 5E 01 D7 04(CH9329 wrapped)，
//   回應 [len][AB CD][func]...，值 = AB 之後第 5 byte 起 7 個 ASCII 字元。
//   重點：電表「框框 S」符號亮著(USB 模組插好、UART 連線中)才寫得成功。
//
// 用法：dmm-meter-agent [--port 8765] [--serial <USB模組序號>] [--poll 400]
// =============================================================================
using System.Management;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using HidLibrary;

const int Vid = 0x1A86, Pid = 0xE429;   // WCH CH9329 USB 通訊模組
const string AgentVersion = "1.0";

int port = ArgInt(args, "--port", 8765);
int pollMs = ArgInt(args, "--poll", 500);
string? serial = ArgStr(args, "--serial", null);

var meter = new Dmm8062A(Vid, Pid);
var state = new MeterState(AgentVersion, port, pollMs, Vid, Pid);

// 背景輪詢電表
var poller = new Thread(() =>
{
    while (true)
    {
        try
        {
            if (!meter.IsOpen) meter.TryOpen(serial);
            var dev = meter.Info();               // 裝置(連線)資訊
            if (meter.IsOpen)
            {
                string? v = meter.ReadValue();    // null = S 沒亮 / 逾時
                state.Update(dev, v is not null, v);
            }
            else
            {
                state.Update(dev, false, null, deviceMissing: true);
            }
        }
        catch (Exception ex)
        {
            state.Update(DeviceInfo.Absent, false, null, error: ex.Message);
            meter.Close();
        }
        Thread.Sleep(pollMs);
    }
})
{ IsBackground = true, Name = "meter-poll" };
poller.Start();

// 本機 HTTP 服務(localhost 免管理員權限)
var listener = new HttpListener();
listener.Prefixes.Add($"http://localhost:{port}/");
listener.Prefixes.Add($"http://127.0.0.1:{port}/");
listener.Start();
string indexHtml = LoadIndexHtml();
Console.WriteLine($"SCMC DMM Meter Agent v{AgentVersion} 已啟動：");
Console.WriteLine($"  監看網頁 → http://localhost:{port}/");
Console.WriteLine($"  讀值 API → http://localhost:{port}/reading");
Console.WriteLine($"  VID=0x{Vid:X4} PID=0x{Pid:X4}  serial={(serial ?? "(第一台)")}  poll={pollMs}ms");
Console.WriteLine("  提醒：電表需按 Hz%/USB 讓『框框 S』亮著才讀得到值。 Ctrl+C 結束。");

while (true)
{
    HttpListenerContext ctx;
    try { ctx = listener.GetContext(); }
    catch { break; }
    try { Handle(ctx, state, indexHtml); } catch { /* 單一請求錯誤不影響服務 */ }
}

// --- HTTP 處理 ---
static void Handle(HttpListenerContext ctx, MeterState state, string indexHtml)
{
    var res = ctx.Response;
    res.Headers["Access-Control-Allow-Origin"] = "*";
    res.Headers["Access-Control-Allow-Methods"] = "GET, OPTIONS";
    res.Headers["Cache-Control"] = "no-store";

    if (ctx.Request.HttpMethod == "OPTIONS") { res.StatusCode = 204; res.Close(); return; }

    string path = ctx.Request.Url?.AbsolutePath ?? "/";
    if (path is "/reading" or "/api/reading" or "/info")
    {
        string json = JsonSerializer.Serialize(state.Snapshot());
        WriteBody(res, "application/json", json);
    }
    else if (path is "/" or "/index.html")
    {
        WriteBody(res, "text/html; charset=utf-8", indexHtml);
    }
    else
    {
        res.StatusCode = 404;
        WriteBody(res, "text/plain; charset=utf-8", "not found");
    }
}

// 內嵌的監看網頁(meter-web-form.html)，供 http://localhost:<port>/
static string LoadIndexHtml()
{
    var asm = Assembly.GetExecutingAssembly();
    string? name = asm.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith("meter-web-form.html", StringComparison.OrdinalIgnoreCase));
    if (name is null) return "<!doctype html><meta charset=utf-8><h3>DMM Meter Agent</h3><p>讀值 API： <code>/reading</code></p>";
    using var s = asm.GetManifestResourceStream(name)!;
    using var r = new StreamReader(s);
    return r.ReadToEnd();
}

static void WriteBody(HttpListenerResponse res, string contentType, string body)
{
    var bytes = Encoding.UTF8.GetBytes(body);
    res.ContentType = contentType;
    res.ContentLength64 = bytes.Length;
    res.OutputStream.Write(bytes, 0, bytes.Length);
    res.Close();
}

static int ArgInt(string[] a, string k, int def)
{
    string? s = ArgStr(a, k, null);
    return int.TryParse(s, out int v) ? v : def;
}
static string? ArgStr(string[] a, string k, string? def)
{
    int i = Array.IndexOf(a, k);
    return (i >= 0 && i + 1 < a.Length) ? a[i + 1] : def;
}

// =============================================================================
// 執行緒安全的最新狀態快取
// =============================================================================
sealed class MeterState
{
    readonly object _lock = new();
    readonly AgentDto _agent;
    DeviceInfo _dev = DeviceInfo.Absent;
    bool _connected;
    string? _value;
    double? _numeric;
    string? _note;
    DateTimeOffset _ts = DateTimeOffset.MinValue;

    public MeterState(string version, int port, int pollMs, int vid, int pid)
        => _agent = new AgentDto(version, port, pollMs, $"0x{vid:X4}", $"0x{pid:X4}");

    public void Update(DeviceInfo dev, bool connected, string? value, bool deviceMissing = false, string? error = null)
    {
        lock (_lock)
        {
            _dev = dev;
            _connected = connected;
            if (connected && value is not null)
            {
                _value = value;
                _numeric = double.TryParse(value, out double n) ? n : null;
                _note = null;
                _ts = DateTimeOffset.Now;
            }
            else
            {
                _note = error ?? (deviceMissing ? "USB 模組未偵測到" : "電表未回應(框框 S 未亮/未連線)");
            }
        }
    }

    public SnapshotDto Snapshot()
    {
        lock (_lock)
        {
            var device = new DeviceDto(_dev.Present, _agent.vid, _agent.pid, _dev.Serial, _dev.Product, _dev.Name, _dev.InstanceId);
            var meter = new MeterDto(
                connected: _connected,
                value: _connected ? _value : null,
                numeric: _connected ? _numeric : null,
                note: _note,
                ts: _ts == DateTimeOffset.MinValue ? null : _ts.ToString("yyyy-MM-ddTHH:mm:ss.fffzzz"));
            return new SnapshotDto(_agent, device, meter);
        }
    }
}

readonly record struct DeviceInfo(bool Present, string? Serial, string? Product, string? Name, string? InstanceId)
{
    public static readonly DeviceInfo Absent = new(false, null, null, null, null);
}

record AgentDto(string version, int port, int pollMs, string vid, string pid);
record DeviceDto(bool present, string vid, string pid, string? serial, string? product, string? name, string? instanceId);
record MeterDto(bool connected, string? value, double? numeric, string? note, string? ts);
record SnapshotDto(AgentDto agent, DeviceDto device, MeterDto meter);

// =============================================================================
// DMM-8062A 讀取(HidLibrary，協定同原廠範例)
// =============================================================================
sealed class Dmm8062A
{
    readonly int _vid, _pid;
    HidDevice? _dev;
    string? _serial, _product, _name, _instanceId;

    public Dmm8062A(int vid, int pid) { _vid = vid; _pid = pid; }
    public bool IsOpen => _dev is { IsOpen: true, IsConnected: true };
    public DeviceInfo Info() => IsOpen ? new DeviceInfo(true, _serial, _product, _name, _instanceId) : DeviceInfo.Absent;

    public bool TryOpen(string? expectedSerial)
    {
        foreach (var d in HidDevices.Enumerate(_vid, _pid))
        {
            string s = d.ReadSerialNumber(out byte[] sn) ? Encoding.Unicode.GetString(sn).TrimEnd('\0') : "";
            if (expectedSerial is not null &&
                !string.Equals(s, expectedSerial, StringComparison.OrdinalIgnoreCase)) continue;
            d.OpenDevice();
            if (d.IsOpen)
            {
                _dev = d;
                _serial = string.IsNullOrEmpty(s) ? null : s;
                _product = d.ReadProduct(out byte[] pn) ? Encoding.Unicode.GetString(pn).TrimEnd('\0') : null;
                (_name, _instanceId) = QueryDeviceManager(d.DevicePath);
                return true;
            }
        }
        return false;
    }

    public void Close() { try { _dev?.CloseDevice(); } catch { } _dev = null; }

    // 送讀值指令(0x5e)並解出量測字串；null = 無回應(S 未亮)
    public string? ReadValue()
    {
        var dev = _dev;
        if (dev is null || !dev.IsOpen) return null;

        byte[] frame = BuildReadFrame(0x5e);
        int outLen = dev.Capabilities.OutputReportByteLength;
        var report = new HidReport(outLen);
        Array.Copy(frame, report.Data, Math.Min(frame.Length, report.Data.Length));

        if (!dev.WriteReport(report)) return null;   // S 未亮時 OUT 無法送出
        var rd = dev.Read(350);
        if (rd.Status != HidDeviceData.ReadStatus.Success) return null;
        return Decode(rd.Data);
    }

    // 回應格式：[reportId][len][AB CD][func][..]，值 = AB 之後第 5 byte 起 7 個 ASCII 字元
    static string? Decode(byte[] d)
    {
        for (int i = 0; i + 12 < d.Length; i++)
        {
            if (d[i] == 0xAB && d[i + 1] == 0xCD)
            {
                string s = Encoding.ASCII.GetString(d, i + 5, 7).Trim();
                return string.IsNullOrWhiteSpace(s) ? null : s;
            }
        }
        return null;
    }

    // 組讀值封包(同原廠 ReadData(CMDByte(0x5e)))：57 AB 00 87 06 AB CD 01 5E 01 D7 04
    static byte[] BuildReadFrame(byte cmd)
    {
        byte[] inner = Checksum2(new byte[] { 0xAB, 0xCD, 0x01, cmd });          // AB CD 01 5E 01 D7
        byte[] head = new byte[] { 0x57, 0xAB, 0x00, 0x87, (byte)inner.Length }; // CH9329 read wrapper
        byte[] data = head.Concat(inner).ToArray();
        int sum = data.Sum(b => b);
        return data.Concat(new byte[] { (byte)(sum >> 8) }).ToArray();
    }

    static byte[] Checksum2(byte[] data)
    {
        int sum = data.Sum(b => b);
        return data.Concat(new byte[] { (byte)(sum >> 8), (byte)(sum & 0xFF) }).ToArray();
    }

    // 查 Windows 裝置管理員(Win32_PnPEntity) 的裝置名稱(FriendlyName) 與執行個體 ID
    static (string? name, string? instanceId) QueryDeviceManager(string? devicePath)
    {
        string? inst = PathToInstanceId(devicePath);
        if (inst is null) return (null, null);
        try
        {
            string wql = "SELECT Name, DeviceID FROM Win32_PnPEntity WHERE DeviceID = '" + inst.Replace("\\", "\\\\") + "'";
            using var searcher = new ManagementObjectSearcher(wql);
            foreach (ManagementBaseObject mo in searcher.Get())
                return (mo["Name"]?.ToString(), mo["DeviceID"]?.ToString() ?? inst);
        }
        catch { /* WMI 不可用時退回只給執行個體 ID */ }
        return (null, inst);
    }

    // \\?\HID#VID_1A86&PID_E429#6&dfbe38&0&0000#{guid} -> HID\VID_1A86&PID_E429\6&DFBE38&0&0000
    static string? PathToInstanceId(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        string s = path.StartsWith(@"\\?\") ? path.Substring(4) : path;
        int g = s.IndexOf("#{", StringComparison.Ordinal);
        if (g >= 0) s = s.Substring(0, g);
        return s.Replace('#', '\\').ToUpperInvariant();
    }
}
