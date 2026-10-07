# Диагностический зонд: запускает Benchmark Tool и печатает все top-level окна,
# принадлежащие процессу и его потомкам, а также отслеживает завершение процесса.
param(
    [string]$Exe = "C:\Program Files (x86)\Steam\steamapps\common\Black Myth Wukong Benchmark Tool\b1\Binaries\Win64\b1-Win64-Shipping.exe",
    [int]$Seconds = 60
)

Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

public static class WinProbe
{
    delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc cb, IntPtr p);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowTextW(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassNameW(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr h, uint cmd);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] static extern IntPtr GetWindowLongPtrW(IntPtr h, int idx);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindowW(string cls, string title);

    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }

    static string Text(IntPtr h)
    {
        var sb = new StringBuilder(512);
        GetWindowTextW(h, sb, sb.Capacity);
        return sb.ToString();
    }

    static string Cls(IntPtr h)
    {
        var sb = new StringBuilder(256);
        GetClassNameW(h, sb, sb.Capacity);
        return sb.ToString();
    }

    public static List<string> Snapshot(uint[] pids)
    {
        var set = new HashSet<uint>(pids);
        var res = new List<string>();
        EnumWindows((h, l) =>
        {
            uint pid;
            GetWindowThreadProcessId(h, out pid);
            if (!set.Contains(pid)) return true;

            RECT r; GetWindowRect(h, out r);
            long style = GetWindowLongPtrW(h, -16).ToInt64();
            long ex = GetWindowLongPtrW(h, -20).ToInt64();
            IntPtr owner = GetWindow(h, 4 /*GW_OWNER*/);
            string title = Text(h);

            res.Add(string.Format(
                "pid={0} hwnd=0x{1:X} class='{2}' title='{3}' visible={4} owner=0x{5:X} rect={6},{7} {8}x{9} style=0x{10:X8} ex=0x{11:X8}",
                pid, h.ToInt64(), Cls(h), title, IsWindowVisible(h), owner.ToInt64(),
                r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top, style, ex));
            return true;
        }, IntPtr.Zero);
        return res;
    }
}
'@

$probeStart = Get-Date

Write-Host "=== Запуск: $Exe" -ForegroundColor Cyan
if (-not (Test-Path $Exe)) { Write-Host "Файл не найден!" -ForegroundColor Red; exit 1 }

$p = Start-Process -FilePath $Exe -WorkingDirectory (Split-Path $Exe) -PassThru
Write-Host "Root PID = $($p.Id)"

$seen = @{}
for ($i = 0; $i -lt $Seconds; $i++) {
    Start-Sleep -Seconds 1

    # дерево процессов: корень + все потомки (по ParentProcessId)
    $all = Get-CimInstance Win32_Process -Property ProcessId, ParentProcessId, Name -ErrorAction SilentlyContinue
    $ids = [System.Collections.Generic.List[uint32]]::new()
    $ids.Add([uint32]$p.Id)
    $changed = $true
    while ($changed) {
        $changed = $false
        foreach ($proc in $all) {
            if ($ids.Contains([uint32]$proc.ParentProcessId) -and -not $ids.Contains([uint32]$proc.ProcessId)) {
                $ids.Add([uint32]$proc.ProcessId); $changed = $true
            }
        }
    }

    $snap = [WinProbe]::Snapshot($ids.ToArray())
    foreach ($s in $snap) {
        if (-not $seen.ContainsKey($s)) {
            $seen[$s] = $true
            Write-Host ("[t={0,3}s] + {1}" -f $i, $s) -ForegroundColor Green
        }
    }

    $exited = $p.HasExited
    if ($exited) {
        Write-Host ("[t={0,3}s] ROOT PROCESS EXITED, exit code = {1}" -f $i, $p.ExitCode) -ForegroundColor Yellow
        break
    }
    if ($i % 5 -eq 0) {
        $names = ($all | Where-Object { $ids.Contains([uint32]$_.ProcessId) } | ForEach-Object { "$($_.Name)($($_.ProcessId))" }) -join ", "
        Write-Host ("[t={0,3}s] живые процессы: {1}; окон найдено: {2}" -f $i, $names, $snap.Count)
    }
}

# остатки
$left = Get-CimInstance Win32_Process -Property ProcessId, Name -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -like "*b1*" -or $_.Name -like "*CrashReport*" }
if ($left) {
    Write-Host "Остаточные процессы: $($left.Name -join ', ')" -ForegroundColor Yellow
    $left | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
    Write-Host "Убиты."
}

Write-Host ""
Write-Host "=== Файлы, изменённые с момента запуска (Saved): " -ForegroundColor Cyan
$b = Split-Path (Split-Path (Split-Path (Split-Path $Exe)))
Get-ChildItem (Join-Path $b "b1\Saved") -Recurse -File -Force -ErrorAction SilentlyContinue |
    Where-Object { $_.LastWriteTime -ge $probeStart } |
    Select-Object LastWriteTime, Length, FullName | Format-Table -AutoSize
