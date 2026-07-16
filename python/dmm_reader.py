"""
Standalone DMM-8062A reader (no vendor software needed).
Protocol from manufacturer sample code (Form1.cs DMM8062A class):
  - poll command 0x5e -> write 57 AB 00 87 06 AB CD 01 5E 01 D7 04 (CH9329 wrapped)
  - write MUST go via interrupt-OUT WriteFile (overlapped); only completes when meter S is on
  - response: [len][AB CD][func][..] ; value = 7 ASCII chars at offset (AB)+5
"""
import ctypes, hid, time
from ctypes import wintypes, byref

VID, PID = 0x1A86, 0xE429
POLL_CMD = [0x57,0xAB,0x00,0x87,0x06,0xAB,0xCD,0x01,0x5E,0x01,0xD7,0x04]

class _OVL(ctypes.Structure):
    _fields_=[("Internal",ctypes.c_void_p),("InternalHigh",ctypes.c_void_p),
              ("Offset",wintypes.DWORD),("OffsetHigh",wintypes.DWORD),("hEvent",wintypes.HANDLE)]

class DMM8062A:
    def __init__(self):
        self.k = ctypes.WinDLL('kernel32', use_last_error=True)
        self.k.CreateFileW.restype=wintypes.HANDLE
        self.k.CreateFileW.argtypes=[wintypes.LPCWSTR,wintypes.DWORD,wintypes.DWORD,wintypes.LPVOID,wintypes.DWORD,wintypes.DWORD,wintypes.HANDLE]
        self.k.WriteFile.restype=wintypes.BOOL
        self.k.WriteFile.argtypes=[wintypes.HANDLE,ctypes.c_void_p,wintypes.DWORD,ctypes.POINTER(wintypes.DWORD),ctypes.c_void_p]
        self.k.GetOverlappedResult.restype=wintypes.BOOL
        self.k.GetOverlappedResult.argtypes=[wintypes.HANDLE,ctypes.c_void_p,ctypes.POINTER(wintypes.DWORD),wintypes.BOOL]
        self.wh=None; self.rh=None
        self._buf=(ctypes.c_ubyte*65)(*([0x00]+POLL_CMD+[0]*(65-1-len(POLL_CMD))))

    def open(self, serial=None):
        devs=[d for d in hid.enumerate(VID,PID)]
        if serial:
            devs=[d for d in devs if (d.get('serial_number') or '')==serial]
        if not devs:
            raise RuntimeError("DMM-8062A USB module not found")
        path=devs[0]['path']
        self.rh=hid.device(); self.rh.open_path(path); self.rh.set_nonblocking(1)
        FILE_FLAG_OVERLAPPED=0x40000000; GENERIC_RW=0xC0000000; SHARE=3; OPEN_EXISTING=3
        self.wh=self.k.CreateFileW(path.decode(),GENERIC_RW,SHARE,None,OPEN_EXISTING,FILE_FLAG_OVERLAPPED,None)
        if not self.wh or self.wh==wintypes.HANDLE(-1).value:
            raise RuntimeError("CreateFile write handle failed err=%d"%ctypes.get_last_error())
        return self

    def _write_cmd(self, timeout_ms=800):
        ev=self.k.CreateEventW(None,True,False,None)
        ov=_OVL(); ov.hEvent=ev
        written=wintypes.DWORD(0)
        r=self.k.WriteFile(self.wh,self._buf,65,byref(written),byref(ov))
        ok=False
        if r:
            ok=True
        else:
            if ctypes.get_last_error()==997:  # IO_PENDING
                if self.k.WaitForSingleObject(ev,timeout_ms)==0:
                    ok=bool(self.k.GetOverlappedResult(self.wh,byref(ov),byref(written),False))
                else:
                    self.k.CancelIo(self.wh)
        self.k.CloseHandle(ev)
        return ok

    @staticmethod
    def _parse(d):
        for i in range(len(d)-12):
            if d[i]==0xAB and d[i+1]==0xCD:
                func=d[i+2]
                vs=bytes(d[i+5:i+12]).decode('latin1').strip()
                try: val=float(vs)
                except: val=None
                return {"func":func,"vstr":vs,"value":val}
        return None

    def read(self, timeout_ms=500):
        """Returns dict{func,vstr,value} or None. Raises RuntimeError if meter not linked (S off)."""
        if not self._write_cmd():
            raise RuntimeError("meter not responding (boxed S off / not linked)")
        t0=time.time()
        while (time.time()-t0)*1000<timeout_ms:
            d=self.rh.read(64)
            if d:
                info=self._parse(d)
                if info: return info
            time.sleep(0.004)
        return None

    def close(self):
        try:
            if self.rh: self.rh.close()
        except: pass
        if self.wh: self.k.CloseHandle(self.wh); self.wh=None

if __name__=="__main__":
    m=DMM8062A().open()
    print("standalone reader started (NO vendor software). Ctrl+C to stop.")
    try:
        for _ in range(20):
            try:
                r=m.read()
                print("  value=%-9s func=0x%02X"%(r["vstr"] if r else "?", r["func"] if r else 0))
            except RuntimeError as e:
                print("  ",e)
            time.sleep(0.5)
    finally:
        m.close()
