using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace ConsoleApp2
{
    class Program
    {
        // ==================== WIN32 API ====================
        [DllImport("user32.dll")]
        static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")]
        static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
        [DllImport("user32.dll")]
        static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);
        [DllImport("user32.dll")]
        static extern bool SetCursorPos(int X, int Y);
        [DllImport("user32.dll")]
        static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, UIntPtr dwExtraInfo);
        [DllImport("user32.dll")]
        static extern bool GetCursorPos(out POINT lpPoint);
        [DllImport("user32.dll")]
        static extern int GetSystemMetrics(int nIndex);

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT { public int X; public int Y; }

        const int SW_RESTORE = 9;
        const byte VK_RETURN = 0x0D;
        const uint KEYEVENTF_KEYUP = 0x0002;
        const int SM_CXSCREEN = 0;
        const int SM_CYSCREEN = 1;

        // ПУТИ
        const string SteamAppId = "3132990";
        const string GameProcessName = "b1-Win64-Shipping";
        const string IniPath = @"C:\Program Files (x86)\Steam\steamapps\common\Black Myth Wukong Benchmark Tool\b1\Saved\Config\Windows\GameUserSettings.ini";
        const string IniBackupPath = IniPath + ".bak";

        // КООРДИНАТЫ КЛИКОВ
        const int MenuItemX = 200;
        const int MenuItemY = 490;

        class BenchmarkResult
        {
            public string ProfileName;      
            public string AverageFps;
            public string MaxFps;
            public string MinFps;
            public string Percentile5;
            public string VideoMemoryUsed;
            public Dictionary<string, string> PcSpecs = new();
            public Dictionary<string, string> GraphicsSettings = new();
        }

        static void Main(string[] args)
        {
            var results = new List<BenchmarkResult>();

            try
            {
                if (!File.Exists(IniBackupPath) && File.Exists(IniPath))
                    File.Copy(IniPath, IniBackupPath);

                // ПРОХОД 1: CPU
                Console.WriteLine("\n===== ПРОХОД 1: CPU =====");
                ApplyIniSettings(GetCpuProfile());
                RunBenchmarkCycle();

                Console.WriteLine("Ожидание экрана результатов (5 сек)...");
                Thread.Sleep(5000);
                var cpuResult = ParseResultScreen("CPU-тест");
                results.Add(cpuResult);

                KillGameProcess();
                Thread.Sleep(5000);

                // ПРОХОД 2: GPU 
                Console.WriteLine("\n===== ПРОХОД 2: GPU =====");
                ApplyIniSettings(GetGpuProfile());
                RunBenchmarkCycle();

                Console.WriteLine("Ожидание экрана результатов (5 сек)...");
                Thread.Sleep(5000);
                var gpuResult = ParseResultScreen("GPU-тест");
                results.Add(gpuResult);

                KillGameProcess();

                // ВЫВОД 
                RestoreIni();
                PrintFinalReport(results);
            }
            catch (Exception ex)
            {
                Console.WriteLine("ОШИБКА: " + ex.Message);
                Console.WriteLine(ex.StackTrace);
                try { RestoreIni(); } catch { }
            }

            Console.WriteLine("\nНажмите любую клавишу для выхода...");
            Console.ReadKey();
        }

        static BenchmarkResult ParseResultScreen(string profileName)
        {
            var result = new BenchmarkResult { ProfileName = profileName };

            int w = GetSystemMetrics(SM_CXSCREEN);
            int h = GetSystemMetrics(SM_CYSCREEN);

            string screenshotPath = Path.Combine(
                Path.GetTempPath(),
                $"bench_{profileName}_{DateTime.Now:HHmmss}.png");

            using (var bmp = new Bitmap(w, h))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    g.CopyFromScreen(0, 0, 0, 0, new Size(w, h));
                }
                bmp.Save(screenshotPath, ImageFormat.Png);
            }
            Console.WriteLine($"Скриншот сохранён: {screenshotPath}");

            string text = OcrImage(screenshotPath).GetAwaiter().GetResult();
            Console.WriteLine("\n--- Распознанный текст ---");
            Console.WriteLine(text);
            Console.WriteLine("--- Конец текста ---\n");

            ParseText(text, result);
            return result;
        }

        static async Task<string> OcrImage(string path)
        {
            var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(path);
            using var stream = await file.OpenAsync(Windows.Storage.FileAccessMode.Read);

            var decoder = await BitmapDecoder.CreateAsync(stream);
            var softwareBitmap = await decoder.GetSoftwareBitmapAsync();

            var engine = OcrEngine.TryCreateFromLanguage(new Language("ru-RU"));
            if (engine == null)
                engine = OcrEngine.TryCreateFromUserProfileLanguages();
            if (engine == null)
                throw new Exception("OCR-движок не доступен");

            var ocrResult = await engine.RecognizeAsync(softwareBitmap);

            var sb = new StringBuilder();
            foreach (var line in ocrResult.Lines)
                sb.AppendLine(line.Text);

            return sb.ToString();
        }

        static void ParseText(string text, BenchmarkResult result)
        {
            var lines = text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();

            for (int i = 0; i < lines.Count; i++)
            {
                string l = lines[i];

                var mAvg = Regex.Match(l, @"^(\d+)\s*FPS$");
                if (mAvg.Success && string.IsNullOrEmpty(result.AverageFps))
                {
                    result.AverageFps = mAvg.Groups[1].Value;
                    continue;
                }

                var mFps = Regex.Match(l, @"^(\d+)\s*FPS$");
                if (mFps.Success)
                {
                    if (string.IsNullOrEmpty(result.MaxFps)) result.MaxFps = mFps.Groups[1].Value;
                    else if (string.IsNullOrEmpty(result.MinFps)) result.MinFps = mFps.Groups[1].Value;
                    continue;
                }

                var mPerc = Regex.Match(l, @"^(\d+)\s*FPS$");
                if (mPerc.Success && string.IsNullOrEmpty(result.Percentile5))
                {
                    result.Percentile5 = mPerc.Groups[1].Value;
                    continue;
                }

                var mVram = Regex.Match(l, @"^([\d,\.]+)\s*(гб|gb|ГБ|GB)", RegexOptions.IgnoreCase);
                if (mVram.Success && string.IsNullOrEmpty(result.VideoMemoryUsed))
                {
                    result.VideoMemoryUsed = mVram.Groups[1].Value + " GB";
                }
            }

            for (int i = 0; i < lines.Count - 1; i++)
            {
                if (lines[i].Contains("Максимум") && lines[i].Contains("Минимум"))
                {
                    var next = lines[i + 1];
                    var nums = Regex.Matches(next, @"(\d+)");
                    if (nums.Count >= 2)
                    {
                        result.MaxFps = nums[0].Groups[1].Value;
                        result.MinFps = nums[1].Groups[1].Value;
                    }
                }
                if (lines[i].Contains("5-й") && lines[i].Contains("перцентиль"))
                {
                    var next = lines[i + 1];
                    var m = Regex.Match(next, @"(\d+)");
                    if (m.Success) result.Percentile5 = m.Groups[1].Value;
                }
            }

            var specLabels = new Dictionary<string, string>
            {
                { "Версия игры", "GameVersion" },
                { "Версия операционной системы", "OS" },
                { "Процессор", "CPU" },
                { "Видеокарта", "GPU" },
                { "Драйвер видеокарты", "GPUDriver" },
                { "Видеопамять", "VRAM" },
                { "Оперативная память", "RAM" },
            };

            for (int i = 0; i < lines.Count - 1; i++)
            {
                foreach (var kv in specLabels)
                {
                    if (lines[i].Equals(kv.Key, StringComparison.OrdinalIgnoreCase))
                    {
                        if (!result.PcSpecs.ContainsKey(kv.Value))
                            result.PcSpecs[kv.Value] = lines[i + 1];
                    }
                }
            }

            var gfxLabels = new[]
            {
                "Режим отображения", "Разрешение экрана", "Набор настроек графики",
                "Степень избыт. выборки сглаживания", "Детализация объектов вдали",
                "Качество сглаживания", "Качество постобработки", "Качество теней",
                "Качество текстур", "Качество волос", "Качество растительности",
                "Размытие при движении", "Полная трассировка лучей",
                "Избыточная выборка сглаживания", "Генерация кадров", "DX12",
            };

            for (int i = 0; i < lines.Count - 1; i++)
            {
                string l = lines[i];
                foreach (var label in gfxLabels)
                {
                    if (l.StartsWith(label, StringComparison.OrdinalIgnoreCase)
                        && !result.GraphicsSettings.ContainsKey(label))
                    {
                        string value = l.Length > label.Length
                            ? l.Substring(label.Length).Trim()
                            : lines[i + 1];

                        if (string.IsNullOrEmpty(value)) value = lines[i + 1];
                        result.GraphicsSettings[label] = value;
                    }
                }
            }
        }

        // ВЫВОД РЕЗУЛЬТАТОВ
        static void PrintFinalReport(List<BenchmarkResult> results)
        {
            Console.WriteLine("\n\n");
            Console.WriteLine("╔══════════════════════════════════════════════════════════╗");
            Console.WriteLine("║              РЕЗУЛЬТАТЫ БЕНЧМАРКА                        ║");
            Console.WriteLine("╚══════════════════════════════════════════════════════════╝");

            var first = results.FirstOrDefault();
            if (first != null && first.PcSpecs.Count > 0)
            {
                Console.WriteLine("\n┌─── ХАРАКТЕРИСТИКИ ПК ───────────────────────────────────");
                foreach (var kv in first.PcSpecs)
                    Console.WriteLine($"│ {kv.Key,-15}: {kv.Value}");
                Console.WriteLine("└─────────────────────────────────────────────────────────");
            }

            foreach (var r in results)
            {
                Console.WriteLine($"\n┌─── {r.ProfileName.ToUpper()} ─────────────────────────────────────");
                Console.WriteLine($"│ Средний FPS         : {r.AverageFps}");
                Console.WriteLine($"│ Максимум FPS        : {r.MaxFps}");
                Console.WriteLine($"│ Минимум FPS         : {r.MinFps}");
                Console.WriteLine($"│ 5-й перцентиль      : {r.Percentile5}");
                Console.WriteLine($"│ Использовано видеопамяти: {r.VideoMemoryUsed}");
                Console.WriteLine("│");
                Console.WriteLine("│ Настройки графики:");
                foreach (var kv in r.GraphicsSettings)
                    Console.WriteLine($"│   {kv.Key,-40}: {kv.Value}");
                Console.WriteLine("└─────────────────────────────────────────────────────────");
            }
        }

        // ЗАПУСК БЕНЧМАРКА
        static void RunBenchmarkCycle()
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "steam://rungameid/" + SteamAppId,
                UseShellExecute = true
            });

            IntPtr hwnd = IntPtr.Zero;
            for (int i = 0; i < 60 && hwnd == IntPtr.Zero; i++)
            {
                Thread.Sleep(2000);
                var procs = Process.GetProcessesByName(GameProcessName);
                if (procs.Length > 0)
                {
                    procs[0].Refresh();
                    if (procs[0].MainWindowHandle != IntPtr.Zero)
                        hwnd = procs[0].MainWindowHandle;
                }
                Console.Write(".");
            }
            if (hwnd == IntPtr.Zero) throw new Exception("Не дождались окна игры.");

            Console.WriteLine("\nЖдём меню (25 сек)...");
            Thread.Sleep(25000);

            ShowWindow(hwnd, SW_RESTORE);
            SetForegroundWindow(hwnd);
            PressEnter();
            Thread.Sleep(2000);

            MoveAndClick(MenuItemX, MenuItemY);
            SetForegroundWindow(hwnd);

            for (int i = 0; i < 40; i++) { Thread.Sleep(5000); Console.Write("."); }
            Console.WriteLine();
        }

        static void KillGameProcess()
        {
            foreach (var p in Process.GetProcessesByName(GameProcessName))
            {
                try
                {
                    p.CloseMainWindow();
                    if (!p.WaitForExit(5000)) p.Kill();
                }
                catch { }
            }
        }

        // INI
        static void ApplyIniSettings(Dictionary<string, string> settings)
        {
            if (!File.Exists(IniPath)) throw new FileNotFoundException(IniPath);
            var lines = new List<string>(File.ReadAllLines(IniPath));
            var applied = new HashSet<string>();
            for (int i = 0; i < lines.Count; i++)
            {
                string t = lines[i].Trim();
                if (t.StartsWith("[") || t.StartsWith(";") || t.StartsWith("#")) continue;
                int eq = t.IndexOf('=');
                if (eq < 0) continue;
                string key = t.Substring(0, eq).Trim();
                if (settings.ContainsKey(key)) { lines[i] = key + "=" + settings[key]; applied.Add(key); }
            }
            foreach (var kv in settings)
                if (!applied.Contains(kv.Key))
                    for (int i = 0; i < lines.Count; i++)
                        if (lines[i].Trim().StartsWith("[/Script/Engine.GameUserSettings]"))
                        { lines.Insert(i + 1, kv.Key + "=" + kv.Value); break; }

            File.WriteAllLines(IniPath, lines, new UTF8Encoding(false));
            Console.WriteLine($"INI: применено {settings.Count} параметров.");
        }

        static void RestoreIni()
        {
            if (File.Exists(IniBackupPath))
            {
                File.Copy(IniBackupPath, IniPath, true);
                Console.WriteLine("INI восстановлен.");
            }
        }

        static Dictionary<string, string> GetCpuProfile() => new()
        {
            { "ResolutionSizeX", "1280" }, { "ResolutionSizeY", "720" },
            { "FullscreenMode", "1" }, { "bUseVSync", "False" },
            { "FrameRateLimit", "0.000000" },
            { "sg.ResolutionQuality", "50.000000" },
            { "sg.ViewDistanceQuality", "3" }, { "sg.AntiAliasingQuality", "0" },
            { "sg.ShadowQuality", "0" }, { "sg.GlobalIlluminationQuality", "0" },
            { "sg.ReflectionQuality", "0" }, { "sg.PostProcessQuality", "0" },
            { "sg.TextureQuality", "0" }, { "sg.EffectsQuality", "0" },
            { "sg.FoliageQuality", "0" }, { "sg.ShadingQuality", "0" },
        };

        static Dictionary<string, string> GetGpuProfile() => new()
        {
            { "ResolutionSizeX", "1920" }, { "ResolutionSizeY", "1080" },
            { "FullscreenMode", "1" }, { "bUseVSync", "False" },
            { "FrameRateLimit", "0.000000" },
            { "sg.ResolutionQuality", "100.000000" },
            { "sg.ViewDistanceQuality", "4" }, { "sg.AntiAliasingQuality", "4" },
            { "sg.ShadowQuality", "4" }, { "sg.GlobalIlluminationQuality", "4" },
            { "sg.ReflectionQuality", "4" }, { "sg.PostProcessQuality", "4" },
            { "sg.TextureQuality", "4" }, { "sg.EffectsQuality", "4" },
            { "sg.FoliageQuality", "4" }, { "sg.ShadingQuality", "4" },
        };

        // ВВОД
        static void MoveAndClick(int x, int y)
        {
            GetCursorPos(out var old);
            for (int i = 1; i <= 10; i++)
            {
                SetCursorPos(old.X + (x - old.X) * i / 10, old.Y + (y - old.Y) * i / 10);
                Thread.Sleep(20);
            }
            SetCursorPos(x, y);
            Thread.Sleep(300);
            PressEnter();
            Thread.Sleep(300);
            PressEnter();
        }

        static void PressEnter()
        {
            keybd_event(VK_RETURN, 0, 0, UIntPtr.Zero);
            Thread.Sleep(100);
            keybd_event(VK_RETURN, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        }
    }
}