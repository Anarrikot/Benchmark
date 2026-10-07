# Discovery probe: start Steam, launch the benchmark via Steam, enumerate windows and capture frames.
param(
    [int]$AppId = 3132990,
    [string]$SteamExe = "C:\Program Files (x86)\Steam\steam.exe",
    [string]$OutDir = "C:\Users\PC-01\source\repos\ConsoleApp4\artifacts",
    [int]$WatchSeconds = 45,
    [int]$ShotEvery = 3,
    [switch]$NoLaunch
)

$ErrorActionPreference = 'Continue'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
if (-not (Test-Path $OutDir)) { New-Item -ItemType Directory -Path $OutDir -Force | Out-Null }

Add-Type -AssemblyName System.Drawing
Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Drawing;
using System.Drawing.Imaging;

public static class Ui
{
    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc cb, IntPtr p);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowTextW(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassNameW(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr h, uint cmd);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] public static extern IntPtr GetWindowLongPtrW(IntPtr h, int idx);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] public static extern IntPtr GetWindowDC(IntPtr h);
    [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, uint data, UIntPtr extra);
    [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
    [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int w, int h);
    [DllImport("gdi32.dll")] public static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [DllImport("gdi32.dll")] public static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, int rop);
    [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr o);
    [DllImport("gdi32.dll")] public static extern bool DeleteDC(IntPtr dc);

    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }

    public static string Title(IntPtr h) { var sb = new StringBuilder(512); GetWindowTextW(h, sb, 512); return sb.ToString(); }
    public static string Cls(IntPtr h) { var sb = new StringBuilder(256); GetClassNameW(h, sb, 256); return sb.ToString(); }

    public class WinInfo
    {
        public uint Pid; public IntPtr Hwnd; public string Class; public string Title;
        public bool Visible; public bool Minimized; public IntPtr Owner; public RECT Rect;
        public long Style; public long Ex;
        public override string ToString()
        {
            return string.Format("pid={0} hwnd=0x{1:X} class='{2}' title='{3}' visible={4} min={5} owner=0x{6:X} rect={7},{8} {9}x{10} style=0x{11:X8} ex=0x{12:X8}",
                Pid, Hwnd.ToInt64(), Class, Title, Visible, Minimized, Owner.ToInt64(),
                Rect.Left, Rect.Top, Rect.Right - Rect.Left, Rect.Bottom - Rect.Top, Style, Ex);
        }
    }

    public static List<WinInfo> All(uint[] pids)
    {
        var set = new HashSet<uint>(pids);
        var list = new List<WinInfo>();
        EnumWindows((h, l) =>
        {
            uint pid; GetWindowThreadProcessId(h, out pid);
            if (!set.Contains(pid)) return true;
            RECT r; GetWindowRect(h, out r);
            list.Add(new WinInfo
            {
                Pid = pid, Hwnd = h, Class = Cls(h), Title = Title(h),
                Visible = IsWindowVisible(h), Minimized = IsIconic(h), Owner = GetWindow(h, 4),
                Rect = r, Style = GetWindowLongPtrW(h, -16).ToInt64(), Ex = GetWindowLongPtrW(h, -20).ToInt64()
            });
            return true;
        }, IntPtr.Zero);
        return list;
    }

    // Returns path of saved png, or null.
    public static string Shot(IntPtr hwnd, string path)
    {
        RECT r; if (!GetWindowRect(hwnd, out r)) return null;
        int w = r.Right - r.Left, h = r.Bottom - r.Top;
        if (w <= 0 || h <= 0) return null;

        using (var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb))
        {
            using (var g = Graphics.FromImage(bmp))
            {
                IntPtr hdc = g.GetHdc();
                bool ok = PrintWindow(hwnd, hdc, 2 /*PW_RENDERFULLCONTENT*/);
                g.ReleaseHdc(hdc);
                if (!ok || IsMostlyBlack(bmp))
                {
                    // fall back to screen capture of that rect
                    IntPtr screenDc = GetDC(IntPtr.Zero);
                    IntPtr memDc = CreateCompatibleDC(screenDc);
                    IntPtr hBmp = CreateCompatibleBitmap(screenDc, w, h);
                    IntPtr old = SelectObject(memDc, hBmp);
                    BitBlt(memDc, 0, 0, w, h, screenDc, r.Left, r.Top, 0x00CC0020 /*SRCCOPY*/);
                    using (var g2 = Graphics.FromImage(bmp))
                    {
                        IntPtr dst = g2.GetHdc();
                        BitBlt(dst, 0, 0, w, h, memDc, 0, 0, 0x00CC0020);
                        g2.ReleaseHdc(dst);
                    }
                    SelectObject(memDc, old);
                    DeleteObject(hBmp); DeleteDC(memDc); ReleaseDC(IntPtr.Zero, screenDc);
                }
            }
            bmp.Save(path, ImageFormat.Png);
        }
        return path;
    }

    static bool IsMostlyBlack(Bitmap bmp)
    {
        int w = bmp.Width, h = bmp.Height, dark = 0, total = 0;
        for (int y = 0; y < h; y += Math.Max(1, h / 24))
            for (int x = 0; x < w; x += Math.Max(1, w / 24))
            {
                var c = bmp.GetPixel(x, y); total++;
                if (c.R < 8 && c.G < 8 && c.B < 8) dark++;
            }
        return total > 0 && dark > total * 0.98;
    }

    public static void Key(byte vk) { keybd_event(vk, 0, 0, UIntPtr.Zero); System.Threading.Thread.Sleep(60); keybd_event(vk, 0, 2, UIntPtr.Zero); }
    public static void Click(int x, int y)
    {
        SetCursorPos(x, y); System.Threading.Thread.Sleep(120);
        mouse_event(0x0002, 0, 0, 0, UIntPtr.Zero); System.Threading.Thread.Sleep(60);
        mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero);
    }
}
'@ -ReferencedAssemblies System.Drawing

function Get-ProcTree([int]$rootPid) {
    $all = Get-CimInstance Win32_Process -ErrorAction SilentlyContinue | Select-Object ProcessId, ParentProcessId, Name
    $ids = New-Object System.Collections.Generic.List[uint32]
    $ids.Add([uint32]$rootPid)
    $changed = $true
    while ($changed) {
        $changed = $false
        foreach ($p in $all) {
            if ($ids.Contains([uint32]$p.ParentProcessId) -and -not $ids.Contains([uint32]$p.ProcessId)) {
                $ids.Add([uint32]$p.ProcessId); $changed = $true
            }
        }
    }
    return ,@{ Ids = $ids; All = $all }
}

# ---------------------------------------------------------------- Steam
$steamRunning = [bool](Get-Process -Name steam -ErrorAction SilentlyContinue)
Write-Host "Steam running: $steamRunning"
if (-not $steamRunning) {
    Write-Host "Starting Steam..."
    Start-Process -FilePath $SteamExe -ArgumentList "-silent" | Out-Null
    for ($i = 0; $i -lt 90; $i++) {
        Start-Sleep -Seconds 1
        $ap = Get-ItemProperty "HKCU:\Software\Valve\Steam\ActiveProcess" -ErrorAction SilentlyContinue
        $alive = $false
        if ($ap.pid) { $alive = [bool](Get-Process -Id $ap.pid -ErrorAction SilentlyContinue) }
        if ($alive) { Write-Host "Steam ready after $($i+1)s, ActiveUser=$($ap.ActiveUser)"; break }
        if ($i % 10 -eq 9) { Write-Host "  waiting for Steam... $($i+1)s" }
    }
}

# ---------------------------------------------------------------- Launch
if (-not $NoLaunch) {
    Write-Host "Launching via steam -applaunch $AppId"
    Start-Process -FilePath $SteamExe -ArgumentList "-applaunch", "$AppId" | Out-Null
}

# ---------------------------------------------------------------- Watch
$shots = 0
$seen = @{}
for ($t = 0; $t -lt $WatchSeconds; $t++) {
    Start-Sleep -Seconds 1
    $procs = Get-Process -ErrorAction SilentlyContinue | Where-Object { $_.ProcessName -match 'b1' -or $_.ProcessName -match 'CrashReport' -or $_.ProcessName -match 'steam' }
    $nameList = ($procs | ForEach-Object { "$($_.ProcessName)($($_.Id))" }) -join ", "
    Write-Host ("[t={0,3}s] procs: {1}" -f $t, $nameList)

    $gameProcs = Get-Process -ErrorAction SilentlyContinue | Where-Object { $_.ProcessName -match 'b1-Win64-Shipping' }
    foreach ($gp in $gameProcs) {
        $tree = Get-ProcTree $gp.Id
        $wins = [Ui]::All($tree.Ids.ToArray())
        foreach ($w in $wins) {
            $key = $w.ToString()
            if (-not $seen.ContainsKey($key)) { $seen[$key] = $true; Write-Host "    + $key" -ForegroundColor Green }
        }
    }

    if (($t % $ShotEvery) -eq 0) {
        foreach ($gp in $gameProcs) {
            $tree = Get-ProcTree $gp.Id
            $wins = [Ui]::All($tree.Ids.ToArray()) | Where-Object { $_.Visible -and $_.Rect.Right - $_.Rect.Left -gt 200 -and $_.Rect.Bottom - $_.Rect.Top -gt 200 } | Sort-Object { -($_.Rect.Right - $_.Rect.Left) * ($_.Rect.Bottom - $_.Rect.Top) }
            if ($wins) {
                $file = Join-Path $OutDir ("shot_{0:d3}s.png" -f $t)
                $saved = [Ui]::Shot($wins[0].Hwnd, $file)
                if ($saved) { $shots++; Write-Host "    shot -> $file  (hwnd 0x$($wins[0].Hwnd.ToInt64().ToString('X')), $($wins[0].Rect.Right - $wins[0].Rect.Left)x$($wins[0].Rect.Bottom - $wins[0].Rect.Top))" }
            }
        }
    }
}

Write-Host "Total shots: $shots"
Write-Host "--- distinct windows seen ---"
$seen.Keys | ForEach-Object { Write-Host $_ }
