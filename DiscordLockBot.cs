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

        // --- P/Invoke Declarations ---
        [DllImport("user32.dll")]
        static extern bool LockWorkStation();

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
                                             "LOCK_COOLDOWN_MS=2000\n";
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


        // --- UI Initialization ---
        private void InitializeComponents()
        {
            trayIcon = new NotifyIcon();
            trayMenu = new ContextMenuStrip();

            // Add Menu Items
            trayMenu.Items.Add("Show Status", null, ShowStatus);
            trayMenu.Items.Add("Run at Startup", null, ToggleStartup);
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
        // Now takes token and channelId as parameters from the loaded config
        private async Task SetupDiscord(string token, ulong channelId) // Takes parameters now
        {
            // Check if already initialized or if config is invalid
            if (_client != null || string.IsNullOrEmpty(token) || channelId == 0)
            {
                Console.WriteLine("Discord setup skipped (already initialized or invalid config).");
                return;
            }

            _client = new DiscordSocketClient(new DiscordSocketConfig
            {
                LogLevel = LogSeverity.Info, // Changed Debug to Info for less console spam
                MessageCacheSize = 50,
                GatewayIntents = GatewayIntents.AllUnprivileged | GatewayIntents.MessageContent
            });

            _client.Log += Log;
            _client.MessageReceived += HandleCommand; // Use the static field _channelId inside
            _client.Ready += Ready; // Use the static field _channelId inside

            SystemEvents.SessionSwitch += SystemEvents_SessionSwitch; // Use the static field _channelId inside

            try
            {
                await _client.LoginAsync(TokenType.Bot, token); // Use parameter
                await _client.StartAsync();
                Console.WriteLine("Discord client started.");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to connect to Discord: {ex.Message}\n\nPlease check your TOKEN in '{ConfigFileName}' and your internet connection.",
                                "Discord Connection Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                // Consider exiting or allowing retry? For now, it just won't connect.
                 _client = null; // Reset client if login fails
            }
        }

        // --- Registry/Startup ---
        private bool IsStartupEnabled()
        {
            // Use try-with-resources for RegistryKey
            using (RegistryKey? key = Registry.CurrentUser.OpenSubKey(StartupKey, false)) // Open read-only
            {
                return key?.GetValue(StartupValue) != null;
            }
        }

        private void ToggleStartup(object? sender, EventArgs e)
        {
            if (sender is not ToolStripMenuItem menuItem) return;

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
                        // Use Assembly.GetExecutingAssembly().Location for reliable path
                        string appPath = Assembly.GetExecutingAssembly().Location;
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

            MessageBox.Show($"Discord Bot Status: {discordStatus}\n" +
                          $"Monitoring Channel ID: {_channelId}\n" + // Show loaded channel ID
                          $"PC Lock Status: {(_wasLocked ? "Locked" : "Unlocked")}\n" +
                          $"Run at Startup: {(IsStartupEnabled() ? "Enabled" : "Disabled")}\n",
                          "Lock Status Monitor",
                          MessageBoxButtons.OK,
                          MessageBoxIcon.Information);
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
            // Ensure client is not null before accessing
            if (_client == null) return;

            // Use the loaded _channelId static field
            _channel = _client.GetChannel(_channelId) as IMessageChannel;
            if (_channel != null)
            {
                Console.WriteLine($"Channel found: {_channel.Name} ({_channelId})");
                await _channel.SendMessageAsync($"🟢 Bot started and monitoring lock status on channel {_channelId}");
            }
            else
            {
                Console.WriteLine($"Error: Channel with ID {_channelId} not found or bot lacks permissions.");
                 MessageBox.Show($"Error: Could not find channel with ID: {_channelId}\n\nPlease check the CHANNEL_ID in '{ConfigFileName}' and ensure the bot is invited to that channel with necessary permissions.",
                                "Discord Channel Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
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
                    await message.Channel.SendMessageAsync($"🖥️ PC Status: **{(_wasLocked ? "Locked" : "Unlocked")}** (Last event: {DateTime.Now.ToString("g")})");
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

                case "!help":
                    await message.Channel.SendMessageAsync(
                        "**📋 Available Commands:**\n" +
                        "`!status` - Check if the monitored computer is currently locked or unlocked.\n" +
                        "`!lock`   - Attempt to lock the monitored computer.\n" +
                        "`!help`   - Shows this help message."
                    );
                    break;

                 // Optional: Add a command to show bot uptime or version?
            }
        }

        private static async void SystemEvents_SessionSwitch(object sender, SessionSwitchEventArgs e)
        {
            try
            {
                if (e.Reason == SessionSwitchReason.SessionLock)
                {
                    _wasLocked = true;
                    Console.WriteLine("Session Locked.");

                    // Capture window positions before lock
                    CaptureWindowSnapshot();

                    // Send Discord notification if connected
                    if (_channel != null && _client?.ConnectionState == ConnectionState.Connected)
                    {
                        await _channel.SendMessageAsync($"🔒 Computer locked at {DateTime.Now:T}"); // T = Short time pattern
                    }
                }
                else if (e.Reason == SessionSwitchReason.SessionUnlock)
                {
                    _wasLocked = false;
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
                    // Verify the window handle is still valid
                    if (!IsWindow(windowData.Handle))
                    {
                        failedCount++;
                        continue;
                    }

                    // Skip snapped windows (halves/quadrants) - Windows handles these correctly
                    if (windowData.SnapState != SnapPosition.None && windowData.SnapState != SnapPosition.Maximized)
                    {
                        skippedCount++;
                        continue;
                    }

                    // Handle maximized windows specially - move to correct monitor then maximize
                    if (windowData.SnapState == SnapPosition.Maximized && windowData.MonitorBounds != Rectangle.Empty)
                    {
                        // Restore window to normal state first
                        ShowWindow(windowData.Handle, SW_RESTORE);

                        // Move window to the center of the saved monitor
                        int centerX = windowData.MonitorBounds.Left + (windowData.MonitorBounds.Width / 2) - 100;
                        int centerY = windowData.MonitorBounds.Top + (windowData.MonitorBounds.Height / 2) - 100;
                        SetWindowPos(windowData.Handle, IntPtr.Zero, centerX, centerY, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);

                        // Maximize it again
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

                // Clear the snapshot after restoration
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
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false); // Recommended for WinForms

            // Load configuration FIRST
            if (!LoadConfiguration())
            {
                // Error messages are shown within LoadConfiguration
                // Don't proceed if config loading fails
                Application.Exit();
                return;
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

            // Setup Discord asynchronously AFTER the UI is initialized but before Application.Run()
            // We use Task.Run to avoid blocking the UI thread during initial connection.
            // We pass the loaded token and channelId.
            _ = Task.Run(() => program.SetupDiscord(_token!, _channelId)); // Use discard _ for fire-and-forget

            // Start the WinForms message loop for the tray icon
            Application.Run();
        }
    }
}