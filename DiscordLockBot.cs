using System;
using System.Drawing;
using System.Windows.Forms;
using Discord;
using Discord.WebSocket;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using System.IO; // Required for file operations
using System.Collections.Generic; // Required for Dictionary
using System.Reflection; // Required for Assembly.GetExecutingAssembly().Location
using System.Diagnostics; // Required for Process.Start
using System.Linq; // Required for LINQ Count()
using System.Threading; // Required for Timer

namespace LockStatusService
{
    // --- WINDOWPLACEMENT Structure for P/Invoke ---
    [StructLayout(LayoutKind.Sequential)]
    public struct WINDOWPLACEMENT
    {
        public uint length;
        public uint flags;
        public uint showCmd;
        public POINT ptMinPosition;
        public POINT ptMaxPosition;
        public RECT rcNormalPosition;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    // --- Snap Position Enum ---
    public enum SnapPosition
    {
        None,           // Regular floating window - restore it
        Maximized,      // Maximized window - restore it
        LeftHalf,       // Snapped to left half - skip
        RightHalf,      // Snapped to right half - skip
        TopLeftQuad,    // Snapped to top-left quadrant - skip
        TopRightQuad,   // Snapped to top-right quadrant - skip
        BottomLeftQuad, // Snapped to bottom-left quadrant - skip
        BottomRightQuad // Snapped to bottom-right quadrant - skip
    }

    // --- Window Data Storage Class ---
    public class WindowData
    {
        public IntPtr Handle { get; set; }
        public WINDOWPLACEMENT Placement { get; set; }
        public SnapPosition SnapState { get; set; } = SnapPosition.None;
        public Rectangle MonitorBounds { get; set; } // For maximized windows - which monitor they were on
    }

    public class Program
    {
        private NotifyIcon? trayIcon;
        private ContextMenuStrip? trayMenu;
        private static DiscordSocketClient? _client;

        // --- Configuration Fields ---
        private static string? _token; // No longer const, loaded from file
        private static ulong _channelId; // No longer const, loaded from file
        private const string ConfigFileName = "config.txt"; // Name of the config file
        private static string? _desktopOKPath; // Path to DesktopOK.exe
        private static string? _desktopOKLayout; // Path to .dok layout file
        private static int _monitorDelayMs = 5000; // Delay before restoring windows (default 5000ms)
        private static int _lockCooldownMs = 2000; // Cooldown to prevent Win+L spam (default 2000ms)

        // --- Display Standby Enforcement ---
        // Apps that assert ES_DISPLAY_REQUIRED (Moonlight, presentation/slideshow windows,
        // media players) keep the monitors lit forever. Master switch is off by default so
        // existing installs behave exactly as before.
        private static bool _displayEnforce = false;
        private static bool _displayKillAuto = false;          // discover blockers via powercfg /requests (needs elevation)
        private static List<string> _displayKillProcesses = new List<string>(); // explicit names, works unelevated
        private static List<string> _displayKillExclude = new List<string>();   // user additions to the protected list
        private static int _displayKillDelayMinutes = 30;      // grace period after lock before sweeping
        private static bool _displayKillForce = true;          // force-kill if a graceful close is ignored
        private static bool _displayForceOff = true;           // blank the panels after sweeping
        private static bool _elevationWarningSent = false;
        private static DateTime _lockedSinceUtc = DateTime.MaxValue; // when the current lock started
        private static int _lockGeneration = 0;                      // bumped per lock; stale sweeps bail out

        // Never closed, regardless of what powercfg reports. A blocker we cannot safely kill
        // is reported, not killed — the fallback is DISPLAY_FORCE_OFF.
        private static readonly HashSet<string> ProtectedProcessNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "system", "idle", "registry", "memory compression",
            "csrss", "wininit", "winlogon", "services", "lsass", "smss", "svchost",
            "explorer", "dwm", "fontdrvhost", "sihost", "ctfmon", "taskhostw",
            "runtimebroker", "searchhost", "startmenuexperiencehost", "shellexperiencehost",
            "logonui", "audiodg", "conhost", "powercfg"
        };

        // --- Keyboard Hook for Win+L Debounce ---
        private static IntPtr _keyboardHookId = IntPtr.Zero;
        private static LowLevelKeyboardProc? _keyboardProc;
        private static DateTime _lastLockKeyTime = DateTime.MinValue;
        private static DateTime _winKeyDownTime = DateTime.MinValue; // When Win key was pressed
        private static int _blockedLockCount = 0;
        private static int _blockedSinceLastAllow = 0; // Tracks blocks since last allowed Win+L
        private static DateTime _lastBlockTime = DateTime.MinValue; // When the last block occurred

        // Keyboard hook constants
        private const int WH_KEYBOARD_LL = 13;
        private const int WM_KEYDOWN = 0x0100;
        private const int WM_KEYUP = 0x0101;
        private const int WM_SYSKEYDOWN = 0x0104;
        private const int WM_SYSKEYUP = 0x0105;
        private const int VK_LWIN = 0x5B;
        private const int VK_RWIN = 0x5C;
        private const int VK_L = 0x4C;

        // Keyboard hook delegate
        private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        private struct KBDLLHOOKSTRUCT
        {
            public uint vkCode;
            public uint scanCode;
            public uint flags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr GetModuleHandle(string? lpModuleName);

        // --- Window Snapshot Storage ---
        private static List<WindowData> _windowSnapshot = new List<WindowData>();
        private static readonly object _snapshotLock = new object();

        // --- Other Fields ---
        private static bool _wasLocked = false;
        private static IMessageChannel? _channel;
        private const string StartupKey = "SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Run";
        private const string StartupValue = "LockStatusMonitor";

        // --- Reliability / Status Tracking ---
        private static readonly DateTime _startTime = DateTime.Now;
        private static DateTime _lastEventTime = DateTime.Now;
        private static string _lastEventDesc = "bot startup";
        private static bool _startupAnnounced = false;   // first Ready announces, reconnects stay quiet
        private static bool _recoveredFromCrash = false; // set in Main if a recent crash marker exists
        private static Mutex? _singleInstanceMutex;
        private static System.Threading.Timer? _watchdogTimer;
        private static int _disconnectedChecks = 0;
        private static bool _rebuildingClient = false;
        private static string? _logFilePath;
        private static readonly object _logLock = new object();
        private const string CrashMarkerFileName = "crashes.txt";

        // --- P/Invoke Declarations ---
        [DllImport("user32.dll")]
        static extern bool LockWorkStation();

        // WTS API - query the real session lock state at startup
        // (SessionSwitch events only fire on *changes*, so the initial state must be queried)
        [DllImport("wtsapi32.dll", SetLastError = true)]
        static extern bool WTSQuerySessionInformation(IntPtr hServer, int sessionId, int wtsInfoClass, out IntPtr ppBuffer, out int pBytesReturned);

        [DllImport("wtsapi32.dll")]
        static extern void WTSFreeMemory(IntPtr pMemory);

        // WTSINFOEX begins with { DWORD Level; union Data; }. The union's LEVEL1 member
        // contains LARGE_INTEGERs, forcing 8-byte alignment, so Data starts at offset 8:
        // SessionId @8, SessionState @12, SessionFlags @16. Explicit offsets avoid
        // marshaling the whole struct.
        [StructLayout(LayoutKind.Explicit)]
        private struct WTSINFOEX_PREFIX
        {
            [FieldOffset(0)] public uint Level;
            [FieldOffset(8)] public uint SessionId;
            [FieldOffset(12)] public uint SessionState;
            [FieldOffset(16)] public int SessionFlags;
        }

        private const int WTSSessionInfoEx = 25;
        private const int WTS_SESSIONSTATE_LOCK = 0;
        private const int WTS_SESSIONSTATE_UNLOCK = 1;

        [DllImport("user32.dll")]
        static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool GetWindowPlacement(IntPtr hWnd, ref WINDOWPLACEMENT lpwndpl);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool SetWindowPlacement(IntPtr hWnd, ref WINDOWPLACEMENT lpwndpl);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool IsWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll")]
        static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        // Delegate for EnumWindows callback
        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        // --- Monitor blanking (display standby enforcement) ---
        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam,
                                                uint fuFlags, uint uTimeout, out IntPtr lpdwResult);

        private static readonly IntPtr HWND_BROADCAST = new IntPtr(0xFFFF);
        private const uint WM_SYSCOMMAND = 0x0112;
        private const int SC_MONITORPOWER = 0xF170;
        private const int MONITOR_OFF = 2;
        private const uint SMTO_ABORTIFHUNG = 0x0002;

        // SetWindowPos flags
        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOZORDER = 0x0004;
        private const uint SWP_NOACTIVATE = 0x0010;

        // ShowWindow commands
        private const int SW_RESTORE = 9;
        private const int SW_MAXIMIZE = 3;

        // Constants for window styles and commands
        private const int GWL_STYLE = -16;
        private const int GWL_EXSTYLE = -20;
        private const uint WS_VISIBLE = 0x10000000;
        private const uint WS_MINIMIZE = 0x20000000;
        private const uint WS_EX_TOOLWINDOW = 0x00000080;
        private const uint WS_EX_NOACTIVATE = 0x08000000;
        private const uint GW_OWNER = 4;
        private const uint SW_SHOWMINIMIZED = 2;
        private const uint SW_SHOWMAXIMIZED = 3;

        // --- Constructor ---
        // Constructor now only initializes UI components. Discord setup happens after config load.
        public Program()
        {
            InitializeComponents();
            // Discord setup is moved to Main after config load
        }

        // --- File Logging ---
        // The app is a WinExe: Console.WriteLine goes nowhere. Redirect it to lockbot.log
        // next to the exe so every existing log line is diagnosable after the fact.
        private sealed class TimestampedFileWriter : TextWriter
        {
            private readonly string _path;
            public TimestampedFileWriter(string path) { _path = path; }
            public override System.Text.Encoding Encoding => System.Text.Encoding.UTF8;
            public override void WriteLine(string? value)
            {
                lock (_logLock)
                {
                    try { File.AppendAllText(_path, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {value}{Environment.NewLine}"); }
                    catch { /* never let logging take the app down */ }
                }
            }
            public override void Write(char value) { } // char-level writes (rare) are dropped
        }

        private static void InitFileLogging()
        {
            try
            {
                _logFilePath = Path.Combine(AppContext.BaseDirectory, "lockbot.log");
                var fi = new FileInfo(_logFilePath);
                if (fi.Exists && fi.Length > 2 * 1024 * 1024)
                {
                    string old = _logFilePath + ".old";
                    File.Delete(old);
                    File.Move(_logFilePath, old);
                }
                Console.SetOut(new TimestampedFileWriter(_logFilePath));
                Console.WriteLine($"=== Lock Status Monitor starting (pid {Environment.ProcessId}) ===");
            }
            catch { /* fall back to default console */ }
        }

        // --- Crash Recovery ---
        private static void InstallCrashHandlers()
        {
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (s, e) => HandleFatalException(e.Exception, "UI thread");
            AppDomain.CurrentDomain.UnhandledException += (s, e) => HandleFatalException(e.ExceptionObject as Exception, "background thread");
            TaskScheduler.UnobservedTaskException += (s, e) =>
            {
                Console.WriteLine($"Unobserved task exception (suppressed): {e.Exception}");
                e.SetObserved();
            };
        }

        private static void HandleFatalException(Exception? ex, string source)
        {
            Console.WriteLine($"FATAL ({source}): {ex}");

            // Best-effort Discord notification, capped at 3 seconds
            try
            {
                if (_channel != null && _client?.ConnectionState == ConnectionState.Connected)
                {
                    Task.Run(() => _channel.SendMessageAsync($"💥 Bot crashed ({source}) — restarting. Check lockbot.log for the stack trace."))
                        .Wait(TimeSpan.FromSeconds(3));
                }
            }
            catch { }

            // Crash-loop guard: allow at most 5 restarts within 10 minutes
            string crashFile = Path.Combine(AppContext.BaseDirectory, CrashMarkerFileName);
            var recent = new List<DateTime>();
            try
            {
                if (File.Exists(crashFile))
                {
                    foreach (string line in File.ReadAllLines(crashFile))
                    {
                        if (DateTime.TryParse(line, null, System.Globalization.DateTimeStyles.RoundtripKind, out DateTime t)
                            && (DateTime.Now - t).TotalMinutes < 10)
                        {
                            recent.Add(t);
                        }
                    }
                }
                recent.Add(DateTime.Now);
                File.WriteAllLines(crashFile, recent.Select(t => t.ToString("o")));
            }
            catch { }

            if (recent.Count <= 5 && Environment.ProcessPath != null)
            {
                Console.WriteLine($"Restarting after crash (restart {recent.Count}/5 in the last 10 minutes)...");
                try { Process.Start(new ProcessStartInfo { FileName = Environment.ProcessPath, UseShellExecute = true }); }
                catch (Exception startEx) { Console.WriteLine($"Failed to relaunch: {startEx.Message}"); }
            }
            else
            {
                Console.WriteLine("Crash loop detected (5+ crashes in 10 minutes) — staying down.");
            }

            try { UninstallKeyboardHook(); } catch { }
            Environment.Exit(1);
        }

        private static void DetectCrashRecovery()
        {
            try
            {
                string crashFile = Path.Combine(AppContext.BaseDirectory, CrashMarkerFileName);
                if (!File.Exists(crashFile)) return;
                var lines = File.ReadAllLines(crashFile);
                if (lines.Length > 0 && DateTime.TryParse(lines[^1], null, System.Globalization.DateTimeStyles.RoundtripKind, out DateTime last)
                    && (DateTime.Now - last).TotalMinutes < 2)
                {
                    _recoveredFromCrash = true;
                    Console.WriteLine($"Detected restart after crash at {last:G}.");
                }
            }
            catch { }
        }

        // --- Initial Lock State ---
        /// <summary>
        /// Queries the actual lock state of the current session so startup status is correct
        /// even when the app starts while the workstation is locked.
        /// </summary>
        private static bool? QueryWorkstationLocked()
        {
            try
            {
                int sessionId = Process.GetCurrentProcess().SessionId;
                if (WTSQuerySessionInformation(IntPtr.Zero, sessionId, WTSSessionInfoEx, out IntPtr buffer, out _))
                {
                    try
                    {
                        var info = Marshal.PtrToStructure<WTSINFOEX_PREFIX>(buffer);
                        if (info.Level == 1)
                        {
                            if (info.SessionFlags == WTS_SESSIONSTATE_LOCK) return true;
                            if (info.SessionFlags == WTS_SESSIONSTATE_UNLOCK) return false;
                        }
                    }
                    finally { WTSFreeMemory(buffer); }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Lock-state query failed: {ex.Message}");
            }
            return null;
        }

        /// <summary>
        /// Live lock state, preferred over the event-tracked flag: sessions that start
        /// already locked (boot -> auto sign-in -> locked) never fire a SessionLock event,
        /// so _wasLocked alone can be stale. Reconciles the flag when they disagree.
        /// </summary>
        private static bool GetCurrentLockState()
        {
            bool? live = QueryWorkstationLocked();
            if (live.HasValue && live.Value != _wasLocked)
            {
                Console.WriteLine($"Lock-state reconciled: event flag said {(_wasLocked ? "Locked" : "Unlocked")}, OS says {(live.Value ? "Locked" : "Unlocked")}.");
                _wasLocked = live.Value;
                _lastEventTime = DateTime.Now;
                _lastEventDesc = live.Value ? "locked (detected by query)" : "unlocked (detected by query)";
            }
            return live ?? _wasLocked;
        }

        // --- Configuration Loading ---
        private static bool LoadConfiguration()
        {
            string exePath = Assembly.GetExecutingAssembly().Location;
            string? exeDir = Path.GetDirectoryName(exePath);
            if (string.IsNullOrEmpty(exeDir))
            {
                MessageBox.Show("Fatal Error: Could not determine application directory.", "Configuration Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return false;
            }
            string configPath = Path.Combine(exeDir, ConfigFileName);

            if (!File.Exists(configPath))
            {
                // Create a template config file if it doesn't exist
                try
                {
                    string templateContent = "# Configuration for Lock Status Monitor\n\n" +
                                             "# Your Discord Bot Token (KEEP THIS SECRET!)\n" +
                                             "TOKEN=YOUR_DISCORD_BOT_TOKEN_HERE\n\n" +
                                             "# The ID of the Discord channel where messages should be sent\n" +
                                             "CHANNEL_ID=YOUR_DISCORD_CHANNEL_ID_HERE\n\n" +
                                             "# --- Display Recovery Settings (Optional) ---\n\n" +
                                             "# Path to DesktopOK folder (contains exe and .dok files)\n" +
                                             "# Download from: https://www.softwareok.com/?seession=DesktopOK\n" +
                                             "# Example: C:\\DesktopOK\n" +
                                             "DESKTOPOK_PATH=\n\n" +
                                             "# Delay in milliseconds before restoring windows after unlock (default: 5000)\n" +
                                             "# Increase if your monitor takes longer to wake up from sleep\n" +
                                             "MONITOR_DELAY_MS=5000\n\n" +
                                             "# --- Win+L Debounce Settings ---\n\n" +
                                             "# Cooldown in milliseconds to prevent Win+L spam from fingerprint reader lock buttons\n" +
                                             "# Set to 0 to disable. Default is 2000ms (2 seconds)\n" +
                                             "LOCK_COOLDOWN_MS=2000\n\n" +
                                             DisplayEnforceConfigTemplate;
                    File.WriteAllText(configPath, templateContent);
                    MessageBox.Show($"Configuration file '{ConfigFileName}' was not found.\n\nA template has been created at:\n{configPath}\n\nPlease edit it with your actual Bot Token and Channel ID, then restart the application.",
                                    "Configuration Needed", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Error creating template configuration file '{configPath}':\n{ex.Message}", "Configuration Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                return false; // Indicate failure, user needs to edit the file
            }

            try
            {
                var configValues = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); // Case-insensitive keys
                string[] lines = File.ReadAllLines(configPath);

                foreach (string line in lines)
                {
                    string trimmedLine = line.Trim();
                    // Skip comments and empty lines
                    if (string.IsNullOrEmpty(trimmedLine) || trimmedLine.StartsWith("#") || trimmedLine.StartsWith(";"))
                        continue;

                    int equalsIndex = trimmedLine.IndexOf('=');
                    if (equalsIndex > 0) // Ensure key is not empty
                    {
                        string key = trimmedLine.Substring(0, equalsIndex).Trim();
                        string value = trimmedLine.Substring(equalsIndex + 1).Trim();
                        if (!string.IsNullOrEmpty(key))
                        {
                            configValues[key] = value;
                        }
                    }
                }

                // Extract values
                if (!configValues.TryGetValue("TOKEN", out _token) || string.IsNullOrWhiteSpace(_token) || _token.Equals("YOUR_DISCORD_BOT_TOKEN_HERE", StringComparison.OrdinalIgnoreCase))
                {
                    MessageBox.Show($"Error: 'TOKEN' is missing, empty, or using the placeholder value in '{ConfigFileName}'.\nPlease add your actual Bot Token.", "Configuration Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    _token = null; // Ensure token is null if invalid
                    return false;
                }

                if (!configValues.TryGetValue("CHANNEL_ID", out string? channelIdStr) || !ulong.TryParse(channelIdStr, out _channelId) || _channelId == 0)
                {
                    MessageBox.Show($"Error: 'CHANNEL_ID' is missing, empty, or not a valid number in '{ConfigFileName}'.\nPlease add your actual Channel ID.", "Configuration Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    _channelId = 0; // Ensure channelId is 0 if invalid
                    return false;
                }

                // Load optional Display Recovery settings
                if (configValues.TryGetValue("DESKTOPOK_PATH", out string? desktopOKPath) && !string.IsNullOrWhiteSpace(desktopOKPath))
                {
                    _desktopOKPath = desktopOKPath;
                    Console.WriteLine($"DesktopOK folder configured: {_desktopOKPath}");
                }
                else
                {
                    Console.WriteLine("DesktopOK folder not configured. Desktop icon restoration will be skipped.");
                }

                // Optional: explicit layout file override
                if (configValues.TryGetValue("DESKTOPOK_LAYOUT", out string? desktopOKLayout) && !string.IsNullOrWhiteSpace(desktopOKLayout))
                {
                    _desktopOKLayout = desktopOKLayout;
                    Console.WriteLine($"DesktopOK layout file override: {_desktopOKLayout}");
                }

                if (configValues.TryGetValue("MONITOR_DELAY_MS", out string? delayStr) && int.TryParse(delayStr, out int delayMs) && delayMs > 0)
                {
                    _monitorDelayMs = delayMs;
                    Console.WriteLine($"Monitor delay configured: {_monitorDelayMs}ms");
                }
                else
                {
                    Console.WriteLine($"Using default monitor delay: {_monitorDelayMs}ms");
                }

                // Load Win+L cooldown setting (prevents fingerprint reader lock button spam)
                if (configValues.TryGetValue("LOCK_COOLDOWN_MS", out string? cooldownStr) && int.TryParse(cooldownStr, out int cooldownMs) && cooldownMs >= 0)
                {
                    _lockCooldownMs = cooldownMs;
                    Console.WriteLine($"Lock cooldown configured: {_lockCooldownMs}ms");
                }
                else
                {
                    Console.WriteLine($"Using default lock cooldown: {_lockCooldownMs}ms");
                }

                // Load Display Standby Enforcement settings (all optional, feature off by default)
                _displayEnforce = ReadBool(configValues, "DISPLAY_STANDBY_ENFORCE", false);
                _displayKillAuto = ReadBool(configValues, "DISPLAY_KILL_AUTO", false);
                _displayKillForce = ReadBool(configValues, "DISPLAY_KILL_FORCE", true);
                _displayForceOff = ReadBool(configValues, "DISPLAY_FORCE_OFF", true);
                _displayKillProcesses = ReadNameList(configValues, "DISPLAY_KILL_PROCESSES");
                _displayKillExclude = ReadNameList(configValues, "DISPLAY_KILL_EXCLUDE");

                if (configValues.TryGetValue("DISPLAY_KILL_DELAY_MINUTES", out string? dkDelayStr) && int.TryParse(dkDelayStr, out int dkDelay) && dkDelay >= 0)
                    _displayKillDelayMinutes = dkDelay;
                if (configValues.ContainsKey("DISPLAY_IDLE_MINUTES"))
                    Console.WriteLine("Config: DISPLAY_IDLE_MINUTES is obsolete and ignored — sweeps only ever run while the PC is locked.");

                if (_displayEnforce)
                {
                    Console.WriteLine($"Display standby enforcement ENABLED: " +
                                      $"list=[{string.Join(", ", _displayKillProcesses)}], auto={_displayKillAuto}, " +
                                      $"delay={_displayKillDelayMinutes}min, force={_displayKillForce}, blank={_displayForceOff}");
                    if (_displayKillProcesses.Count == 0 && !_displayKillAuto)
                        Console.WriteLine("Display standby enforcement: nothing to close (DISPLAY_KILL_PROCESSES empty and DISPLAY_KILL_AUTO=false).");
                    if (_displayKillAuto && !IsElevated())
                        Console.WriteLine("Display standby enforcement: DISPLAY_KILL_AUTO needs an elevated process — auto-discovery will be skipped.");
                }
                else
                {
                    Console.WriteLine("Display standby enforcement disabled (DISPLAY_STANDBY_ENFORCE=false).");
                }

                // Success!
                Console.WriteLine("Configuration loaded successfully.");
                return true;
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error reading configuration file '{configPath}':\n{ex.Message}", "Configuration Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return false;
            }
        }


        // --- Display Standby Enforcement ---

        private const string DisplayEnforceConfigTemplate =
            "# --- Display Standby Enforcement (Optional) ---\n" +
            "# Apps that assert a DISPLAY power request (Moonlight, presentation/slideshow\n" +
            "# windows, media players) keep the monitors lit indefinitely. When enabled, the\n" +
            "# app closes those programs after you lock so the panels can go into standby.\n" +
            "# Master switch. Everything below is ignored unless this is true.\n" +
            "DISPLAY_STANDBY_ENFORCE=false\n\n" +
            "# Comma-separated process names to close (\".exe\" optional). Works without admin.\n" +
            "# Example: DISPLAY_KILL_PROCESSES=Moonlight,POWERPNT,vlc\n" +
            "DISPLAY_KILL_PROCESSES=\n\n" +
            "# Also auto-discover blockers via 'powercfg /requests' and close them.\n" +
            "# REQUIRES the app to run elevated; skipped (with a warning) otherwise.\n" +
            "DISPLAY_KILL_AUTO=false\n\n" +
            "# Names never closed, on top of the built-in system-process protection list.\n" +
            "DISPLAY_KILL_EXCLUDE=\n\n" +
            "# Minutes the PC must stay CONTINUOUSLY locked before anything is closed.\n" +
            "# Unlocking during the wait cancels the sweep. Default 30.\n" +
            "DISPLAY_KILL_DELAY_MINUTES=30\n\n" +
            "# Force-kill a process that ignores the polite close request (default true).\n" +
            "# Set false if you would rather keep unsaved work than guarantee standby.\n" +
            "DISPLAY_KILL_FORCE=true\n\n" +
            "# After sweeping, tell the monitors to power off immediately. This is the OLED\n" +
            "# safety net for a blocker that could not be closed.\n" +
            "DISPLAY_FORCE_OFF=true\n";

        private static bool ReadBool(Dictionary<string, string> cfg, string key, bool fallback)
        {
            if (!cfg.TryGetValue(key, out string? raw) || string.IsNullOrWhiteSpace(raw)) return fallback;
            raw = raw.Trim();
            if (raw.Equals("true", StringComparison.OrdinalIgnoreCase) || raw == "1" || raw.Equals("yes", StringComparison.OrdinalIgnoreCase)) return true;
            if (raw.Equals("false", StringComparison.OrdinalIgnoreCase) || raw == "0" || raw.Equals("no", StringComparison.OrdinalIgnoreCase)) return false;
            Console.WriteLine($"Config: '{key}={raw}' is not a boolean — using {fallback}.");
            return fallback;
        }

        /// <summary>Parses a comma-separated process-name list, normalizing away any ".exe".</summary>
        private static List<string> ReadNameList(Dictionary<string, string> cfg, string key)
        {
            var result = new List<string>();
            if (!cfg.TryGetValue(key, out string? raw) || string.IsNullOrWhiteSpace(raw)) return result;
            foreach (string part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                string name = part.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? part[..^4] : part;
                if (name.Length > 0 && !result.Contains(name, StringComparer.OrdinalIgnoreCase)) result.Add(name);
            }
            return result;
        }

        private static bool IsElevated()
        {
            try
            {
                using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
                return new System.Security.Principal.WindowsPrincipal(identity)
                    .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
            }
            catch { return false; }
        }

        private static bool IsProtectedName(string name) =>
            ProtectedProcessNames.Contains(name) ||
            _displayKillExclude.Contains(name, StringComparer.OrdinalIgnoreCase) ||
            string.Equals(name, Process.GetCurrentProcess().ProcessName, StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Runs 'powercfg /requests' and returns its output, or null if it could not be read
        /// (not elevated, missing binary, timeout). Never throws.
        /// </summary>
        private static string? RunPowercfgRequests()
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "powercfg.exe",
                    Arguments = "/requests",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                using var p = Process.Start(psi);
                if (p == null) return null;
                string output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
                if (!p.WaitForExit(10000))
                {
                    try { p.Kill(); } catch { }
                    Console.WriteLine("powercfg /requests timed out.");
                    return null;
                }
                // Unelevated, powercfg exits 1 with "This command requires administrator
                // privileges...". Treat any non-zero exit or permission wording as "unknown"
                // rather than "nothing is blocking" — an empty result must never green-light a kill.
                if (p.ExitCode != 0 ||
                    output.Contains("administrator privileges", StringComparison.OrdinalIgnoreCase) ||
                    output.Contains("do not have permission", StringComparison.OrdinalIgnoreCase) ||
                    output.Contains("Access is denied", StringComparison.OrdinalIgnoreCase))
                {
                    Console.WriteLine($"powercfg /requests unavailable (exit {p.ExitCode}) — the app is likely not elevated.");
                    return null;
                }
                if (!output.Contains("DISPLAY:", StringComparison.OrdinalIgnoreCase))
                {
                    Console.WriteLine("powercfg /requests returned no DISPLAY section — output not understood, ignoring.");
                    return null;
                }
                return output;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"powercfg /requests failed: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Extracts the process names holding a DISPLAY power request. Returns null when the
        /// request list could not be read at all — the caller must treat that as "unknown"
        /// and close nothing, rather than as "nothing is blocking".
        /// </summary>
        private static List<string>? GetDisplayRequestProcesses(out List<string> unkillable, string? rawOutput = null)
        {
            unkillable = new List<string>();
            string? output = rawOutput ?? RunPowercfgRequests();
            if (output == null) return null;

            var names = new List<string>();
            bool inDisplaySection = false;

            foreach (string line in output.Split('\n'))
            {
                string trimmed = line.Trim();
                if (trimmed.Length == 0) continue;

                // Section headers are bare uppercase words ending in ':' at the start of a line
                if (trimmed.EndsWith(":") && trimmed.Length > 1 && trimmed[..^1].All(char.IsLetter))
                {
                    inDisplaySection = trimmed.Equals("DISPLAY:", StringComparison.OrdinalIgnoreCase);
                    continue;
                }
                if (!inDisplaySection) continue;
                if (trimmed.Equals("None.", StringComparison.OrdinalIgnoreCase)) continue;

                if (trimmed.StartsWith("[PROCESS]", StringComparison.OrdinalIgnoreCase))
                {
                    // e.g. [PROCESS] \Device\HarddiskVolume3\...\Moonlight.exe
                    string path = trimmed["[PROCESS]".Length..].Trim();
                    string file = path.Split('\\').LastOrDefault() ?? string.Empty;
                    if (file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) file = file[..^4];
                    if (file.Length == 0) continue;

                    if (IsProtectedName(file)) { unkillable.Add(file); continue; }
                    if (!names.Contains(file, StringComparer.OrdinalIgnoreCase)) names.Add(file);
                }
                else if (trimmed.StartsWith("[DRIVER]", StringComparison.OrdinalIgnoreCase) ||
                         trimmed.StartsWith("[SERVICE]", StringComparison.OrdinalIgnoreCase))
                {
                    unkillable.Add(trimmed);
                }
            }

            return names;
        }

        /// <summary>
        /// Closes every running instance of the named processes in the current session:
        /// polite CloseMainWindow first, then Kill if DISPLAY_KILL_FORCE is set.
        /// </summary>
        private static async Task<(List<string> closed, List<string> survived)> CloseProcessesAsync(IEnumerable<string> names)
        {
            var closed = new List<string>();
            var survived = new List<string>();
            int mySession = Process.GetCurrentProcess().SessionId;

            foreach (string name in names.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (IsProtectedName(name))
                {
                    Console.WriteLine($"Display sweep: '{name}' is on the protected list — not closing.");
                    continue;
                }

                Process[] instances;
                try { instances = Process.GetProcessesByName(name); }
                catch (Exception ex) { Console.WriteLine($"Display sweep: cannot enumerate '{name}': {ex.Message}"); continue; }

                foreach (var proc in instances)
                {
                    try
                    {
                        if (proc.Id == Environment.ProcessId) continue;
                        if (proc.SessionId != mySession) continue; // never touch other sessions / services
                        if (proc.HasExited) continue;

                        Console.WriteLine($"Display sweep: closing {name} (pid {proc.Id})...");
                        bool asked = false;
                        try { asked = proc.CloseMainWindow(); } catch { }

                        if (asked && proc.WaitForExit(5000))
                        {
                            closed.Add($"{name} (pid {proc.Id})");
                            continue;
                        }

                        if (_displayKillForce)
                        {
                            proc.Kill(entireProcessTree: true);
                            if (proc.WaitForExit(5000)) closed.Add($"{name} (pid {proc.Id}, force-killed)");
                            else survived.Add($"{name} (pid {proc.Id})");
                        }
                        else
                        {
                            Console.WriteLine($"Display sweep: {name} (pid {proc.Id}) ignored the close request and DISPLAY_KILL_FORCE=false.");
                            survived.Add($"{name} (pid {proc.Id})");
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Display sweep: failed to close {name}: {ex.Message}");
                        survived.Add($"{name} (error: {ex.Message})");
                    }
                    finally { proc.Dispose(); }
                }
            }

            await Task.CompletedTask;
            return (closed, survived);
        }

        /// <summary>Broadcasts the "turn the panels off" request. The OLED safety net.</summary>
        private static void ForceMonitorsOff()
        {
            try
            {
                SendMessageTimeout(HWND_BROADCAST, WM_SYSCOMMAND, (IntPtr)SC_MONITORPOWER, (IntPtr)MONITOR_OFF,
                                   SMTO_ABORTIFHUNG, 3000, out _);
                Console.WriteLine("Display sweep: monitor power-off broadcast sent.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Display sweep: monitor power-off failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Schedules the post-lock sweep. No-op when the feature is off, so callers do not
        /// have to check.
        /// </summary>
        private static void ArmDisplaySweep(string trigger)
        {
            if (!_displayEnforce) return;
            _ = Task.Run(() => RunDisplaySweepAsync(trigger, requireStillLocked: true));
        }

        /// <summary>
        /// The sweep: close configured (and, if elevated and enabled, auto-discovered)
        /// DISPLAY blockers, then optionally blank the monitors.
        /// </summary>
        private static async Task RunDisplaySweepAsync(string trigger, bool requireStillLocked)
        {
            if (!_displayEnforce) return;

            try
            {
                if (requireStillLocked)
                {
                    // Wait until the session has been *continuously* locked for the grace period.
                    // Polling (rather than one long Task.Delay) means an unlock aborts promptly and
                    // a stale sweep left over from an earlier lock cannot fire early on a new one.
                    int myGeneration = _lockGeneration;
                    Console.WriteLine($"Display sweep ({trigger}): waiting for {_displayKillDelayMinutes} continuous locked minutes...");

                    while (true)
                    {
                        if (myGeneration != _lockGeneration)
                        {
                            Console.WriteLine("Display sweep: superseded by a newer lock — aborting.");
                            return;
                        }
                        if (!GetCurrentLockState())
                        {
                            Console.WriteLine("Display sweep: PC was unlocked during the grace period — aborting.");
                            return;
                        }

                        double lockedMinutes = (DateTime.UtcNow - _lockedSinceUtc).TotalMinutes;
                        if (lockedMinutes >= _displayKillDelayMinutes) break;

                        double minutesLeft = _displayKillDelayMinutes - lockedMinutes;
                        await Task.Delay(TimeSpan.FromSeconds(Math.Min(30, Math.Max(1, minutesLeft * 60))));
                    }
                }

                // Hard gate: this feature exists to protect the panels while you are away.
                // If the session is unlocked, you are using the machine — close nothing.
                if (!GetCurrentLockState())
                {
                    Console.WriteLine($"Display sweep ({trigger}): PC is unlocked — nothing will be closed.");
                    await TrySendAsync("🌙 Display sweep skipped — the PC is unlocked. Sweeps only run while it is locked.");
                    return;
                }

                var targets = new List<string>(_displayKillProcesses);
                var unkillable = new List<string>();
                bool autoRan = false;

                if (_displayKillAuto)
                {
                    if (!IsElevated())
                    {
                        Console.WriteLine("Display sweep: skipping auto-discovery (not elevated).");
                        if (!_elevationWarningSent)
                        {
                            _elevationWarningSent = true;
                            await TrySendAsync("⚠️ DISPLAY_KILL_AUTO is on but the bot is not running elevated — " +
                                               "auto-discovery is skipped. Only DISPLAY_KILL_PROCESSES will be closed.");
                        }
                    }
                    else
                    {
                        var discovered = GetDisplayRequestProcesses(out unkillable);
                        if (discovered == null)
                        {
                            // Could not read the request list. Unknown != empty: close nothing extra.
                            Console.WriteLine("Display sweep: could not read power requests — auto-discovery contributed nothing.");
                        }
                        else
                        {
                            autoRan = true;
                            Console.WriteLine($"Display sweep: powercfg reports DISPLAY blockers: " +
                                              $"{(discovered.Count == 0 ? "(none)" : string.Join(", ", discovered))}");
                            targets.AddRange(discovered);
                        }
                    }
                }

                var (closed, survived) = await CloseProcessesAsync(targets);

                // Re-check what is still holding the display awake after the sweep
                string remaining = "";
                if (autoRan)
                {
                    var after = GetDisplayRequestProcesses(out var stillUnkillable);
                    var all = new List<string>();
                    if (after != null) all.AddRange(after);
                    all.AddRange(stillUnkillable);
                    if (all.Count > 0) remaining = string.Join(", ", all.Distinct(StringComparer.OrdinalIgnoreCase));
                }
                else if (unkillable.Count > 0)
                {
                    remaining = string.Join(", ", unkillable.Distinct(StringComparer.OrdinalIgnoreCase));
                }

                if (_displayForceOff) ForceMonitorsOff();

                Console.WriteLine($"Display sweep ({trigger}) done: closed {closed.Count}, survived {survived.Count}, " +
                                  $"still blocking: {(remaining.Length == 0 ? "(none)" : remaining)}");

                if (closed.Count > 0 || survived.Count > 0 || remaining.Length > 0)
                {
                    string msg = $"🌙 Display standby sweep ({trigger}):";
                    if (closed.Count > 0) msg += $"\n✅ Closed: {string.Join(", ", closed)}";
                    if (survived.Count > 0) msg += $"\n❌ Would not close: {string.Join(", ", survived)}";
                    if (remaining.Length > 0) msg += $"\n⚠️ Still holding the display awake: {remaining}";
                    if (_displayForceOff) msg += "\n🖥️ Monitors told to power off.";
                    await TrySendAsync(msg);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Display sweep ({trigger}) error: {ex}");
            }
        }

        /// <summary>Best-effort Discord send; never throws, never blocks the caller's logic.</summary>
        private static async Task TrySendAsync(string message)
        {
            try
            {
                if (_channel != null && _client?.ConnectionState == ConnectionState.Connected)
                    await _channel.SendMessageAsync(message);
            }
            catch (Exception ex) { Console.WriteLine($"Failed to send Discord message: {ex.Message}"); }
        }

        // --- UI Initialization ---
        private void InitializeComponents()
        {
            trayIcon = new NotifyIcon();
            trayMenu = new ContextMenuStrip();

            // Add Menu Items
            trayMenu.Items.Add("Show Status", null, ShowStatus);
            trayMenu.Items.Add("Run at Startup", null, ToggleStartup);
            trayMenu.Items.Add("Sweep Display Blockers Now", null, SweepDisplayNow);
            trayMenu.Items.Add("-"); // Separator
            trayMenu.Items.Add("Exit", null, Exit);

            // Configure Tray Icon
            trayIcon.Icon = SystemIcons.Application; // Consider adding a custom icon to your project resources
            trayIcon.Text = "Lock Status Monitor";
            trayIcon.ContextMenuStrip = trayMenu;
            trayIcon.Visible = true;

            // Set initial check state for startup menu item
            var startupMenuItem = trayMenu.Items[1] as ToolStripMenuItem;
            if (startupMenuItem != null)
            {
                startupMenuItem.Checked = IsStartupEnabled();
            }
        }

        // --- Discord Setup ---
        // Retries forever with backoff: at boot the network is often not up yet, and the old
        // behavior (one attempt -> modal error box -> dead client, no retry) is why the
        // startup status message was unreliable.
        private static async Task SetupDiscord(string token, ulong channelId)
        {
            if (string.IsNullOrEmpty(token) || channelId == 0)
            {
                Console.WriteLine("Discord setup skipped (invalid config).");
                return;
            }

            var client = new DiscordSocketClient(new DiscordSocketConfig
            {
                LogLevel = LogSeverity.Info,
                MessageCacheSize = 50,
                GatewayIntents = GatewayIntents.AllUnprivileged | GatewayIntents.MessageContent
            });

            client.Log += Log;
            client.MessageReceived += HandleCommand;
            client.Ready += Ready;
            client.Disconnected += ex =>
            {
                Console.WriteLine($"Discord disconnected: {ex?.Message ?? "(no exception)"} — Discord.Net will auto-reconnect.");
                return Task.CompletedTask;
            };

            _client = client;

            int attempt = 0;
            while (_client == client) // abort if the watchdog swapped in a new client
            {
                try
                {
                    await client.LoginAsync(TokenType.Bot, token);
                    await client.StartAsync();
                    Console.WriteLine("Discord client started.");
                    return;
                }
                catch (Exception ex)
                {
                    attempt++;
                    if (ex.Message.Contains("401") || ex.Message.Contains("Unauthorized", StringComparison.OrdinalIgnoreCase))
                    {
                        Console.WriteLine($"Discord login rejected (bad token?): {ex.Message}. Not retrying.");
                        MessageBox.Show($"Discord rejected the bot token. Check TOKEN in '{ConfigFileName}'.",
                                        "Discord Connection Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        return;
                    }
                    int delaySeconds = Math.Min(300, 10 * attempt);
                    Console.WriteLine($"Discord connect attempt {attempt} failed: {ex.Message}. Retrying in {delaySeconds}s...");
                    await Task.Delay(TimeSpan.FromSeconds(delaySeconds));
                }
            }
        }

        // --- Connection Watchdog ---
        // Discord.Net normally auto-reconnects on its own; this catches the rare stuck
        // client by tearing it down and rebuilding after ~3 minutes of continuous disconnect.
        private static async void WatchdogTick(object? state)
        {
            try
            {
                // Reconcile the event-tracked lock flag with the OS; if a change was missed
                // (boot-time race, events lost during sleep), send the notification late
                bool before = _wasLocked;
                bool now = GetCurrentLockState();
                if (now != before)
                {
                    // A lock event we never saw (boot-time race, events lost during sleep) still
                    // needs to arm the display sweep, or the monitors stay lit for the whole trip.
                    if (now) { _lockedSinceUtc = DateTime.UtcNow; Interlocked.Increment(ref _lockGeneration); ArmDisplaySweep("late-detected lock"); }
                    else { _lockedSinceUtc = DateTime.MaxValue; }

                    if (_channel != null && _client?.ConnectionState == ConnectionState.Connected)
                    {
                        try
                        {
                            await _channel.SendMessageAsync(now
                                ? $"🔒 Computer is locked (state change detected late, at {DateTime.Now:T})"
                                : $"🔓 Computer is unlocked (state change detected late, at {DateTime.Now:T})");
                        }
                        catch (Exception ex) { Console.WriteLine($"Watchdog: failed to send reconcile notification: {ex.Message}"); }
                    }
                }

                if (_rebuildingClient) return;
                if (_client != null && _client.ConnectionState == ConnectionState.Connected)
                {
                    _disconnectedChecks = 0;
                    return;
                }
                _disconnectedChecks++;
                Console.WriteLine($"Watchdog: Discord not connected (check {_disconnectedChecks}/3).");
                if (_disconnectedChecks >= 3)
                {
                    _rebuildingClient = true;
                    _disconnectedChecks = 0;
                    Console.WriteLine("Watchdog: rebuilding Discord client...");
                    var old = _client;
                    _client = null;
                    _channel = null;
                    if (old != null)
                    {
                        try { await old.StopAsync(); old.Dispose(); }
                        catch (Exception ex) { Console.WriteLine($"Watchdog: error disposing old client: {ex.Message}"); }
                    }
                    await SetupDiscord(_token!, _channelId);
                    _rebuildingClient = false;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Watchdog error: {ex.Message}");
                _rebuildingClient = false;
            }
        }

        // --- Registry/Startup ---
        /// <summary>
        /// True when a scheduled task named StartupValue exists. DISPLAY_KILL_AUTO needs an
        /// elevated process, which the HKCU Run key cannot provide — so autostart may have been
        /// moved to a "run with highest privileges" logon task instead.
        /// </summary>
        private static bool IsScheduledTaskStartup()
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "schtasks.exe",
                    Arguments = $"/Query /TN \"{StartupValue}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                using var p = Process.Start(psi);
                if (p == null) return false;
                p.StandardOutput.ReadToEnd();
                p.StandardError.ReadToEnd();
                if (!p.WaitForExit(5000)) { try { p.Kill(); } catch { } return false; }
                return p.ExitCode == 0;
            }
            catch { return false; }
        }

        private bool IsStartupEnabled()
        {
            if (IsScheduledTaskStartup()) return true;

            // Use try-with-resources for RegistryKey
            using (RegistryKey? key = Registry.CurrentUser.OpenSubKey(StartupKey, false)) // Open read-only
            {
                return key?.GetValue(StartupValue) != null;
            }
        }

        private void ToggleStartup(object? sender, EventArgs e)
        {
            if (sender is not ToolStripMenuItem menuItem) return;

            // A logon task outranks the Run key; toggling the registry value here would leave
            // the app still starting from the task while the menu claimed otherwise.
            if (IsScheduledTaskStartup())
            {
                MessageBox.Show($"Autostart is handled by the scheduled task \"{StartupValue}\" " +
                                "(needed so the app runs elevated for DISPLAY_KILL_AUTO).\n\n" +
                                "Manage it in Task Scheduler, or delete it with:\n" +
                                $"schtasks /Delete /TN \"{StartupValue}\" /F",
                                "Lock Status Monitor", MessageBoxButtons.OK, MessageBoxIcon.Information);
                menuItem.Checked = true;
                return;
            }

            try
            {
                // Use try-with-resources for RegistryKey
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(StartupKey, true) ?? Registry.CurrentUser.CreateSubKey(StartupKey)) // Open writable or create if not exists
                {
                    if (IsStartupEnabled())
                    {
                        key.DeleteValue(StartupValue, false); // Do not throw if not found
                        menuItem.Checked = false;
                        Console.WriteLine("Removed from startup.");
                    }
                    else
                    {
                        // Environment.ProcessPath = the apphost .exe. Assembly.Location returns
                        // the .dll on .NET Core, which Windows cannot launch from the Run key.
                        string appPath = Environment.ProcessPath ?? Application.ExecutablePath;
                        // Enclose path in quotes in case it contains spaces
                        key.SetValue(StartupValue, $"\"{appPath}\"");
                        menuItem.Checked = true;
                        Console.WriteLine("Added to startup.");
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to update startup settings: {ex.Message}\n\nTry running the application as Administrator if you encounter permission issues.", "Startup Error",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                 // Optionally reset the checkbox state if update failed
                 menuItem.Checked = IsStartupEnabled();
            }
        }

        /// <summary>
        /// Rewrites an existing Run-key entry that points at the wrong file. Older versions
        /// wrote the .dll path (Assembly.Location on .NET Core), which Windows cannot launch,
        /// so autostart silently failed.
        /// </summary>
        private static void RepairStartupEntry()
        {
            try
            {
                string? exePath = Environment.ProcessPath;
                if (exePath == null) return;
                if (IsScheduledTaskStartup())
                {
                    // The logon task owns autostart; leave the Run key alone so we don't
                    // resurrect a second, unelevated launch path.
                    Console.WriteLine($"Autostart is provided by the scheduled task '{StartupValue}' — skipping Run-key repair.");
                    return;
                }
                using RegistryKey? key = Registry.CurrentUser.OpenSubKey(StartupKey, true);
                string? current = key?.GetValue(StartupValue) as string;
                if (key != null && current != null && !string.Equals(current, $"\"{exePath}\"", StringComparison.OrdinalIgnoreCase))
                {
                    key.SetValue(StartupValue, $"\"{exePath}\"");
                    Console.WriteLine($"Repaired startup entry: '{current}' -> '\"{exePath}\"'");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Could not verify startup entry: {ex.Message}");
            }
        }

        // --- Tray Menu Actions ---
        private void ShowStatus(object? sender, EventArgs e)
        {
             string discordStatus = "Disconnected";
             if (_client != null) {
                 discordStatus = _client.ConnectionState.ToString();
                 if (_client.ConnectionState == ConnectionState.Connected && _client.LoginState != LoginState.LoggedIn) {
                    discordStatus = "Connecting..."; // More specific state
                 } else if (_client.ConnectionState == ConnectionState.Connected && _channel == null) {
                    discordStatus = "Connected (Channel Invalid?)";
                 }
             }

            string displayStatus = _displayEnforce
                ? $"On (list: {(_displayKillProcesses.Count == 0 ? "none" : string.Join(", ", _displayKillProcesses))}" +
                  $"{(_displayKillAuto ? ", auto" + (IsElevated() ? "" : " [needs elevation]") : "")})"
                : "Off";

            MessageBox.Show($"Discord Bot Status: {discordStatus}\n" +
                          $"Monitoring Channel ID: {_channelId}\n" + // Show loaded channel ID
                          $"PC Lock Status: {(_wasLocked ? "Locked" : "Unlocked")}\n" +
                          $"Run at Startup: {(IsStartupEnabled() ? "Enabled" : "Disabled")}\n" +
                          $"Display Standby Enforcement: {displayStatus}\n" +
                          $"Log File: {_logFilePath}\n",
                          "Lock Status Monitor",
                          MessageBoxButtons.OK,
                          MessageBoxIcon.Information);
        }

        private void SweepDisplayNow(object? sender, EventArgs e)
        {
            if (!_displayEnforce)
            {
                MessageBox.Show("Display standby enforcement is disabled.\n\n" +
                                $"Set DISPLAY_STANDBY_ENFORCE=true in {ConfigFileName} and restart the app.",
                                "Lock Status Monitor", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            _ = Task.Run(() => RunDisplaySweepAsync("tray menu", requireStillLocked: false));
        }

        private void Exit(object? sender, EventArgs e)
        {
            // Try to send shutdown message asynchronously but don't wait forever
            var shutdownTask = Task.Run(async () => {
                 if (_channel != null && _client?.ConnectionState == ConnectionState.Connected)
                 {
                    try {
                         await _channel.SendMessageAsync("🔴 Bot shutting down...");
                    } catch (Exception ex) {
                        Console.WriteLine($"Failed to send shutdown message: {ex.Message}");
                    }
                 }
            });

            // Give it a short time to complete
            shutdownTask.Wait(TimeSpan.FromSeconds(2));

            // Cleanup
            _watchdogTimer?.Dispose();
            UninstallKeyboardHook(); // Remove keyboard hook
            SystemEvents.SessionSwitch -= SystemEvents_SessionSwitch; // Unsubscribe
            if (_client != null)
            {
                _client.LogoutAsync().GetAwaiter().GetResult(); // Logout cleanly
                _client.Dispose();
            }
            if (trayIcon != null)
            {
                trayIcon.Visible = false;
                trayIcon.Dispose();
            }
            Application.Exit();
        }

        // --- Discord Event Handlers ---
        private static async Task Ready()
        {
            try
            {
                if (_client == null) return;

                // Re-resolve the channel on every Ready (it fires again after reconnects).
                // The socket cache can lag right after Ready, so retry briefly and fall
                // back to a REST fetch before giving up — a failed one-shot lookup here
                // used to leave _channel null forever (bot running but silent).
                IMessageChannel? channel = _client.GetChannel(_channelId) as IMessageChannel;
                for (int i = 0; i < 5 && channel == null; i++)
                {
                    await Task.Delay(2000);
                    channel = _client.GetChannel(_channelId) as IMessageChannel
                              ?? await _client.Rest.GetChannelAsync(_channelId) as IMessageChannel;
                }
                _channel = channel;

                if (channel == null)
                {
                    Console.WriteLine($"Error: Channel {_channelId} not found or bot lacks permissions (will retry on next reconnect).");
                    return;
                }

                Console.WriteLine($"Channel found: {channel.Name} ({_channelId})");

                if (!_startupAnnounced)
                {
                    _startupAnnounced = true;
                    string prefix = _recoveredFromCrash ? "🟠 Bot recovered after a crash" : "🟢 Bot started";
                    // Re-query at announce time: at boot the bot can launch a moment before
                    // Windows applies the auto-sign-in lock, so the state read in Main races it
                    await channel.SendMessageAsync($"{prefix} — PC is currently **{(GetCurrentLockState() ? "Locked" : "Unlocked")}**. Send `!help` for commands.");
                }
                else
                {
                    Console.WriteLine("Reconnected to Discord gateway.");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error in Ready handler: {ex.Message}");
            }
        }

        private static Task Log(LogMessage msg)
        {
            // Simple console logging
            Console.WriteLine($"[{msg.Severity}] {msg.Source}: {msg.Message} {msg.Exception}");
            return Task.CompletedTask;
        }

        private static async Task HandleCommand(SocketMessage message)
        {
            // Ensure message is valid, from the correct channel, and not from a bot
            if (message == null || message.Channel.Id != _channelId || message.Author.IsBot || _client == null)
                return;

            string command = message.Content.Trim().ToLowerInvariant(); // Use InvariantCulture for commands

            switch (command)
            {
                case "!status":
                    var uptime = DateTime.Now - _startTime;
                    await message.Channel.SendMessageAsync(
                        $"🖥️ PC Status: **{(GetCurrentLockState() ? "Locked" : "Unlocked")}**\n" +
                        $"Last change: {_lastEventDesc} at {_lastEventTime:g}\n" +
                        $"Bot uptime: {(int)uptime.TotalDays}d {uptime.Hours}h {uptime.Minutes}m");
                    break;

                case "!lock":
                    if (LockWorkStation())
                    {
                        await message.Channel.SendMessageAsync("🔒 Locking computer via remote command...");
                        // Note: The SessionSwitch event will handle the actual status update and message
                    }
                    else
                    {
                        // GetLastWin32Error might give more info, but this is usually sufficient
                        await message.Channel.SendMessageAsync("❌ Failed to lock computer. This might require specific permissions or user interaction.");
                    }
                    break;

                case "!restart":
                    await message.Channel.SendMessageAsync("🔄 Restarting bot...");
                    Console.WriteLine("Restart requested via Discord command.");
                    if (Environment.ProcessPath != null)
                    {
                        Process.Start(new ProcessStartInfo { FileName = Environment.ProcessPath, UseShellExecute = true });
                    }
                    Environment.Exit(0);
                    break;

                case "!display":
                {
                    if (!_displayEnforce)
                    {
                        await message.Channel.SendMessageAsync("🌙 Display standby enforcement is **off** (DISPLAY_STANDBY_ENFORCE=false).");
                        break;
                    }

                    string cfg = $"🌙 **Display standby enforcement: on**\n" +
                                 $"Watch list: {(_displayKillProcesses.Count == 0 ? "(none)" : string.Join(", ", _displayKillProcesses))}\n" +
                                 $"Auto-discovery: {(_displayKillAuto ? (IsElevated() ? "on" : "on but NOT elevated — inactive") : "off")}\n" +
                                 $"Delay after lock: {_displayKillDelayMinutes} min · Force-kill: {_displayKillForce}\n" +
                                 $"Blank monitors: {_displayForceOff}\n" +
                                 $"Only ever acts while the PC is locked. Currently: **{(GetCurrentLockState() ? "Locked" : "Unlocked")}**";

                    if (IsElevated())
                    {
                        var blockers = GetDisplayRequestProcesses(out var unkillable);
                        cfg += blockers == null
                            ? "\n\nCurrent DISPLAY requests: could not be read."
                            : $"\n\nCurrent DISPLAY requests: {(blockers.Count == 0 && unkillable.Count == 0 ? "none — the monitors are free to sleep" : string.Join(", ", blockers.Concat(unkillable)))}";
                    }
                    else
                    {
                        cfg += "\n\n(Run the bot elevated to see the live `powercfg /requests` DISPLAY list.)";
                    }

                    await message.Channel.SendMessageAsync(cfg);
                    break;
                }

                case "!sweep":
                    if (!_displayEnforce)
                    {
                        await message.Channel.SendMessageAsync("🌙 Display standby enforcement is off — nothing to sweep.");
                        break;
                    }
                    await message.Channel.SendMessageAsync("🌙 Running display standby sweep now...");
                    _ = Task.Run(() => RunDisplaySweepAsync("manual", requireStillLocked: false));
                    break;

                case "!help":
                    await message.Channel.SendMessageAsync(
                        "**📋 Available Commands:**\n" +
                        "`!status`  - Check lock state, last change, and bot uptime.\n" +
                        "`!lock`    - Attempt to lock the monitored computer.\n" +
                        "`!display` - Show display-standby settings and what is keeping the monitors awake.\n" +
                        "`!sweep`   - Close display blockers now (skips the wait; still requires the PC to be locked).\n" +
                        "`!restart` - Restart the bot application.\n" +
                        "`!help`    - Shows this help message."
                    );
                    break;
            }
        }

        private static async void SystemEvents_SessionSwitch(object sender, SessionSwitchEventArgs e)
        {
            try
            {
                if (e.Reason == SessionSwitchReason.SessionLock)
                {
                    _wasLocked = true;
                    _lastEventTime = DateTime.Now;
                    _lastEventDesc = "locked";
                    _lockedSinceUtc = DateTime.UtcNow;
                    Interlocked.Increment(ref _lockGeneration);
                    Console.WriteLine("Session Locked.");

                    // Capture window positions before lock
                    CaptureWindowSnapshot();

                    // Send Discord notification if connected
                    if (_channel != null && _client?.ConnectionState == ConnectionState.Connected)
                    {
                        await _channel.SendMessageAsync($"🔒 Computer locked at {DateTime.Now:T}"); // T = Short time pattern
                    }

                    // Close whatever is holding the display awake so the panels can sleep
                    ArmDisplaySweep("lock");
                }
                else if (e.Reason == SessionSwitchReason.SessionUnlock)
                {
                    _wasLocked = false;
                    _lastEventTime = DateTime.Now;
                    _lastEventDesc = "unlocked";
                    _lockedSinceUtc = DateTime.MaxValue;
                    Console.WriteLine("Session Unlocked.");

                    // Send Discord notification if connected
                    if (_channel != null && _client?.ConnectionState == ConnectionState.Connected)
                    {
                        await _channel.SendMessageAsync($"🔓 Computer unlocked at {DateTime.Now:T}"); // T = Short time pattern
                    }

                    // Fire-and-forget the display recovery (runs on background thread, won't block UI)
                    _ = Task.Run(async () => await RestoreWindowsAsync());
                }
                // You could potentially handle other reasons like ConsoleConnect/Disconnect if needed
            }
            catch (Exception ex)
            {
                 Console.WriteLine($"Error in session switch handler: {ex.Message}");
                 // Maybe try resending later or log persistently?
            }
        }

        // --- Window Snapshot Methods ---

        /// <summary>
        /// Detects if a window is snapped to a screen edge/corner based on its position.
        /// </summary>
        private static SnapPosition DetectSnapPosition(IntPtr hWnd, WINDOWPLACEMENT placement)
        {
            // Check if maximized first
            if (placement.showCmd == SW_SHOWMAXIMIZED)
                return SnapPosition.Maximized;

            // Get actual window rect
            if (!GetWindowRect(hWnd, out RECT windowRect))
                return SnapPosition.None;

            int windowWidth = windowRect.Right - windowRect.Left;
            int windowHeight = windowRect.Bottom - windowRect.Top;

            // Find which screen this window is on
            Rectangle windowBounds = new Rectangle(windowRect.Left, windowRect.Top, windowWidth, windowHeight);
            Screen? screen = Screen.FromRectangle(windowBounds);
            if (screen == null)
                return SnapPosition.None;

            Rectangle workArea = screen.WorkingArea;
            int halfWidth = workArea.Width / 2;
            int halfHeight = workArea.Height / 2;

            // Tolerance for snap detection (pixels)
            const int tolerance = 20;

            bool leftAligned = Math.Abs(windowRect.Left - workArea.Left) < tolerance;
            bool rightAligned = Math.Abs(windowRect.Right - workArea.Right) < tolerance;
            bool topAligned = Math.Abs(windowRect.Top - workArea.Top) < tolerance;
            bool bottomAligned = Math.Abs(windowRect.Bottom - workArea.Bottom) < tolerance;
            bool isHalfWidth = Math.Abs(windowWidth - halfWidth) < tolerance;
            bool isFullWidth = Math.Abs(windowWidth - workArea.Width) < tolerance;
            bool isHalfHeight = Math.Abs(windowHeight - halfHeight) < tolerance;
            bool isFullHeight = Math.Abs(windowHeight - workArea.Height) < tolerance;

            // Detect quadrant snaps (4-grid)
            if (isHalfWidth && isHalfHeight)
            {
                if (leftAligned && topAligned) return SnapPosition.TopLeftQuad;
                if (rightAligned && topAligned) return SnapPosition.TopRightQuad;
                if (leftAligned && bottomAligned) return SnapPosition.BottomLeftQuad;
                if (rightAligned && bottomAligned) return SnapPosition.BottomRightQuad;
            }

            // Detect half snaps
            if (isHalfWidth && isFullHeight)
            {
                if (leftAligned) return SnapPosition.LeftHalf;
                if (rightAligned) return SnapPosition.RightHalf;
            }

            return SnapPosition.None;
        }


        /// <summary>
        /// Captures the position and placement of all visible, non-minimized windows.
        /// Called when the session is locked.
        /// </summary>
        private static void CaptureWindowSnapshot()
        {
            lock (_snapshotLock)
            {
                _windowSnapshot.Clear();

                EnumWindows((hWnd, lParam) =>
                {
                    // Skip if window is not visible
                    if (!IsWindowVisible(hWnd))
                        return true; // Continue enumeration

                    // Get window styles
                    int style = GetWindowLong(hWnd, GWL_STYLE);
                    int exStyle = GetWindowLong(hWnd, GWL_EXSTYLE);

                    // Skip minimized windows
                    if ((style & (int)WS_MINIMIZE) != 0)
                        return true;

                    // Skip tool windows (tooltips, floating toolbars, etc.)
                    if ((exStyle & (int)WS_EX_TOOLWINDOW) != 0)
                        return true;

                    // Skip windows with WS_EX_NOACTIVATE (system overlay windows)
                    if ((exStyle & (int)WS_EX_NOACTIVATE) != 0)
                        return true;

                    // Skip windows that are owned by other windows (child windows/dialogs)
                    IntPtr owner = GetWindow(hWnd, GW_OWNER);
                    if (owner != IntPtr.Zero)
                        return true;

                    // Get window placement
                    WINDOWPLACEMENT placement = new WINDOWPLACEMENT();
                    placement.length = (uint)Marshal.SizeOf(typeof(WINDOWPLACEMENT));

                    if (GetWindowPlacement(hWnd, ref placement))
                    {
                        // Only save if not minimized (double-check via showCmd)
                        if (placement.showCmd != SW_SHOWMINIMIZED)
                        {
                            SnapPosition snapState = DetectSnapPosition(hWnd, placement);

                            // For maximized windows, save which monitor they're on
                            Rectangle monitorBounds = Rectangle.Empty;
                            if (snapState == SnapPosition.Maximized && GetWindowRect(hWnd, out RECT rect))
                            {
                                Rectangle windowBounds = new Rectangle(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
                                Screen? screen = Screen.FromRectangle(windowBounds);
                                if (screen != null)
                                {
                                    monitorBounds = screen.WorkingArea;
                                }
                            }

                            _windowSnapshot.Add(new WindowData
                            {
                                Handle = hWnd,
                                Placement = placement,
                                SnapState = snapState,
                                MonitorBounds = monitorBounds
                            });
                        }
                    }

                    return true; // Continue enumeration
                }, IntPtr.Zero);

                // Count snap states for logging
                int snappedCount = _windowSnapshot.Count(w => w.SnapState != SnapPosition.None && w.SnapState != SnapPosition.Maximized);
                int maximizedCount = _windowSnapshot.Count(w => w.SnapState == SnapPosition.Maximized);
                int regularCount = _windowSnapshot.Count(w => w.SnapState == SnapPosition.None);
                Console.WriteLine($"Window snapshot captured: {_windowSnapshot.Count} total ({regularCount} regular, {maximizedCount} maximized, {snappedCount} snapped).");
            }
        }

        /// <summary>
        /// Restores window positions from the captured snapshot.
        /// Called after unlock with a delay for monitor handshake.
        /// </summary>
        private static async Task RestoreWindowsAsync()
        {
            // Wait for monitor to complete handshake
            Console.WriteLine($"Display Recovery: Waiting {_monitorDelayMs}ms for monitor handshake...");
            await Task.Delay(_monitorDelayMs);

            // Step A: Launch DesktopOK to restore desktop icons
            // _desktopOKPath should point to the DesktopOK folder (not the exe)
            // We'll find both the exe and .dok files in that folder
            if (!string.IsNullOrEmpty(_desktopOKPath) && Directory.Exists(_desktopOKPath))
            {
                try
                {
                    // Find DesktopOK executable
                    string? exePath = null;
                    string[] exeCandidates = new[] { "DesktopOK_x64.exe", "DesktopOK.exe", "DesktopOK_x32.exe" };
                    foreach (var exe in exeCandidates)
                    {
                        string candidate = Path.Combine(_desktopOKPath, exe);
                        if (File.Exists(candidate))
                        {
                            exePath = candidate;
                            break;
                        }
                    }

                    if (string.IsNullOrEmpty(exePath))
                    {
                        Console.WriteLine($"Display Recovery: No DesktopOK executable found in {_desktopOKPath}");
                    }
                    else
                    {
                        // Find most recent .dok file
                        string? layoutFile = _desktopOKLayout;
                        if (string.IsNullOrEmpty(layoutFile))
                        {
                            var dokFiles = Directory.GetFiles(_desktopOKPath, "*.dok");
                            if (dokFiles.Length > 0)
                            {
                                layoutFile = dokFiles.OrderByDescending(f => File.GetLastWriteTime(f)).First();
                                Console.WriteLine($"Display Recovery: Found layout file: {layoutFile}");
                            }
                        }

                        if (!string.IsNullOrEmpty(layoutFile) && File.Exists(layoutFile))
                        {
                            string arguments = $"/load /silent \"{layoutFile}\"";
                            Console.WriteLine($"Display Recovery: Running {exePath} with args: {arguments}");

                            ProcessStartInfo psi = new ProcessStartInfo
                            {
                                FileName = exePath,
                                Arguments = arguments,
                                UseShellExecute = false,
                                CreateNoWindow = true
                            };
                            Process.Start(psi);
                            Console.WriteLine("Display Recovery: DesktopOK executed successfully.");
                        }
                        else
                        {
                            Console.WriteLine($"Display Recovery: No .dok layout file found in {_desktopOKPath}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Display Recovery: Error with DesktopOK: {ex.Message}");
                }
            }
            else if (!string.IsNullOrEmpty(_desktopOKPath))
            {
                Console.WriteLine($"Display Recovery: DesktopOK folder not found: {_desktopOKPath}");
            }

            // Step B: Restore window positions
            int restoredCount = 0;
            int skippedCount = 0;
            int failedCount = 0;

            lock (_snapshotLock)
            {
                Console.WriteLine($"Display Recovery: Processing {_windowSnapshot.Count} windows...");

                foreach (var windowData in _windowSnapshot)
                {
                    if (!IsWindow(windowData.Handle))
                    {
                        failedCount++;
                        continue;
                    }

                    // Skip snapped windows — Windows snap state can't be restored programmatically
                    // (SetWindowPos positions correctly but loses snap behavior, keyboard simulation is too fragile)
                    if (windowData.SnapState != SnapPosition.None && windowData.SnapState != SnapPosition.Maximized)
                    {
                        skippedCount++;
                        continue;
                    }

                    // Handle maximized windows specially - move to correct monitor then maximize
                    if (windowData.SnapState == SnapPosition.Maximized && windowData.MonitorBounds != Rectangle.Empty)
                    {
                        ShowWindow(windowData.Handle, SW_RESTORE);
                        int centerX = windowData.MonitorBounds.Left + (windowData.MonitorBounds.Width / 2) - 100;
                        int centerY = windowData.MonitorBounds.Top + (windowData.MonitorBounds.Height / 2) - 100;
                        SetWindowPos(windowData.Handle, IntPtr.Zero, centerX, centerY, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
                        ShowWindow(windowData.Handle, SW_MAXIMIZE);
                        restoredCount++;
                        continue;
                    }

                    // Restore regular floating windows using placement
                    WINDOWPLACEMENT placement = windowData.Placement;
                    if (SetWindowPlacement(windowData.Handle, ref placement))
                    {
                        restoredCount++;
                    }
                    else
                    {
                        failedCount++;
                    }
                }

                _windowSnapshot.Clear();
            }

            Console.WriteLine($"Display Recovery: Restored {restoredCount} windows, skipped {skippedCount} snapped, {failedCount} failed/invalid.");

            // Send Discord notification about restoration
            if (_channel != null && _client?.ConnectionState == ConnectionState.Connected)
            {
                try
                {
                    await _channel.SendMessageAsync($"🖥️ Display Recovery completed: {restoredCount} windows restored.");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Display Recovery: Failed to send Discord notification: {ex.Message}");
                }
            }
        }

        // --- Keyboard Hook for Win+L Debounce ---

        /// <summary>
        /// Installs a low-level keyboard hook to intercept and debounce Win+L keypresses.
        /// This prevents fingerprint reader lock buttons from spamming the lock command.
        /// </summary>
        private static void InstallKeyboardHook()
        {
            if (_keyboardHookId != IntPtr.Zero)
            {
                Console.WriteLine("Keyboard hook already installed.");
                return;
            }

            _keyboardProc = KeyboardHookCallback;
            using var curProcess = Process.GetCurrentProcess();
            using var curModule = curProcess.MainModule!;
            _keyboardHookId = SetWindowsHookEx(WH_KEYBOARD_LL, _keyboardProc, GetModuleHandle(curModule.ModuleName), 0);

            if (_keyboardHookId == IntPtr.Zero)
            {
                Console.WriteLine($"Failed to install keyboard hook: {Marshal.GetLastWin32Error()}");
            }
            else
            {
                Console.WriteLine($"Keyboard hook installed. Win+L cooldown: {_lockCooldownMs}ms");
            }
        }

        /// <summary>
        /// Uninstalls the keyboard hook.
        /// </summary>
        private static void UninstallKeyboardHook()
        {
            if (_keyboardHookId != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_keyboardHookId);
                _keyboardHookId = IntPtr.Zero;
                Console.WriteLine($"Keyboard hook uninstalled. Blocked {_blockedLockCount} repeated Win+L attempts.");
            }
        }

        /// <summary>
        /// Keyboard hook callback. Intercepts Win+L and blocks repeated presses within the cooldown period.
        /// </summary>
        private static IntPtr KeyboardHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                var hookStruct = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                int vk = (int)hookStruct.vkCode;
                int msg = wParam.ToInt32();
                var now = DateTime.Now;

                // Track Win key state with timestamp (prevents stale state issues)
                if (vk == VK_LWIN || vk == VK_RWIN)
                {
                    if (msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN)
                    {
                        _winKeyDownTime = now;
                    }
                    else if (msg == WM_KEYUP || msg == WM_SYSKEYUP)
                    {
                        _winKeyDownTime = DateTime.MinValue;
                    }
                }

                // Check for L key while Win is held (Win+L combo)
                // Win key must have been pressed within the last 1 second to count as a combo
                bool winKeyIsDown = _winKeyDownTime != DateTime.MinValue &&
                                    (now - _winKeyDownTime).TotalMilliseconds < 1000;

                if (vk == VK_L && winKeyIsDown && (msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN))
                {
                    var timeSinceLastLock = (now - _lastLockKeyTime).TotalMilliseconds;

                    if (timeSinceLastLock < _lockCooldownMs)
                    {
                        // Block the repeated Win+L - we're within cooldown
                        _blockedLockCount++;
                        _blockedSinceLastAllow++;
                        _lastBlockTime = now;
                        Console.WriteLine($"Blocked Win+L (cooldown: {timeSinceLastLock:F0}ms < {_lockCooldownMs}ms) [Total blocked: {_blockedLockCount}]");
                        return (IntPtr)1; // Block the keystroke
                    }
                    else
                    {
                        // Allow the Win+L and reset timer
                        _lastLockKeyTime = now;
                        Console.WriteLine($"Win+L allowed (time since last: {timeSinceLastLock:F0}ms)");

                        // Report blocked attempts to Discord, but only if they happened recently (within 10 seconds)
                        // This avoids confusing reports from ghost presses that happened hours ago
                        if (_blockedSinceLastAllow > 0)
                        {
                            int blocked = _blockedSinceLastAllow;
                            var timeSinceLastBlock = (now - _lastBlockTime).TotalSeconds;
                            _blockedSinceLastAllow = 0;

                            // Only report if blocks happened within the last 10 seconds (actual spam)
                            if (timeSinceLastBlock <= 10)
                            {
                                _ = Task.Run(async () =>
                                {
                                    if (_channel != null && _client?.ConnectionState == ConnectionState.Connected)
                                    {
                                        try
                                        {
                                            await _channel.SendMessageAsync($"🛡️ Blocked {blocked} repeated Win+L attempt{(blocked > 1 ? "s" : "")} (fingerprint reader spam)");
                                        }
                                        catch (Exception ex)
                                        {
                                            Console.WriteLine($"Failed to send block notification: {ex.Message}");
                                        }
                                    }
                                });
                            }
                            else
                            {
                                Console.WriteLine($"Discarded {blocked} old block(s) from {timeSinceLastBlock:F0}s ago (ghost press)");
                            }
                        }
                    }
                }
            }

            return CallNextHookEx(_keyboardHookId, nCode, wParam, lParam);
        }

        // --- Main Entry Point ---
        [STAThread]
        static void Main()
        {
            InitFileLogging();
            InstallCrashHandlers();

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false); // Recommended for WinForms

            // Single instance: wait briefly instead of failing so !restart and
            // crash-restart handoffs (old process still exiting) work
            _singleInstanceMutex = new Mutex(false, "Local\\LockStatusMonitor_SingleInstance");
            try
            {
                if (!_singleInstanceMutex.WaitOne(TimeSpan.FromSeconds(15), false))
                {
                    Console.WriteLine("Another instance is already running — exiting.");
                    return;
                }
            }
            catch (AbandonedMutexException)
            {
                // Previous instance died without releasing — we own the mutex now
            }

            // Load configuration FIRST
            if (!LoadConfiguration())
            {
                // Error messages are shown within LoadConfiguration
                return;
            }

            DetectCrashRecovery();
            RepairStartupEntry();

            // Initialize lock state from the OS instead of assuming Unlocked
            bool? locked = QueryWorkstationLocked();
            if (locked.HasValue)
            {
                _wasLocked = locked.Value;
                if (_wasLocked) _lockedSinceUtc = DateTime.UtcNow; // unknown true start; treat startup as the beginning
                Console.WriteLine($"Initial lock state: {(_wasLocked ? "Locked" : "Unlocked")}");
            }
            else
            {
                Console.WriteLine("Initial lock state unknown — assuming Unlocked.");
            }

            // Configuration loaded successfully, now create the application instance
            var program = new Program();

            // Install keyboard hook for Win+L debounce (prevents fingerprint reader spam)
            if (_lockCooldownMs > 0)
            {
                InstallKeyboardHook();
            }
            else
            {
                Console.WriteLine("Win+L debounce disabled (LOCK_COOLDOWN_MS=0).");
            }

            // Lock/unlock tracking works even while Discord is down; subscribe once here
            // (previously inside SetupDiscord, which would double-subscribe on client rebuilds)
            SystemEvents.SessionSwitch += SystemEvents_SessionSwitch;

            // Started up into an already-locked session (reboot -> auto sign-in -> locked):
            // no SessionLock event will ever fire, so arm the sweep here.
            if (_wasLocked) ArmDisplaySweep("startup while locked");

            // Setup Discord asynchronously; it retries internally until it connects
            _ = Task.Run(() => SetupDiscord(_token!, _channelId));

            // Watchdog rebuilds the Discord client if it stays disconnected too long
            _watchdogTimer = new System.Threading.Timer(WatchdogTick, null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));

            // Start the WinForms message loop for the tray icon
            Application.Run();
        }
    }
}