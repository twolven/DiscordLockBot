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

namespace LockStatusService
{
    public class Program
    {
        private NotifyIcon? trayIcon;
        private ContextMenuStrip? trayMenu;
        private static DiscordSocketClient? _client;

        // --- Configuration Fields ---
        private static string? _token; // No longer const, loaded from file
        private static ulong _channelId; // No longer const, loaded from file
        private const string ConfigFileName = "config.txt"; // Name of the config file

        // --- Other Fields ---
        private static bool _wasLocked = false;
        private static IMessageChannel? _channel;
        private const string StartupKey = "SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Run";
        private const string StartupValue = "LockStatusMonitor";

        [DllImport("user32.dll")]
        static extern bool LockWorkStation();

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
                                             "CHANNEL_ID=YOUR_DISCORD_CHANNEL_ID_HERE\n";
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
            // Check if Discord client is connected and channel is valid
            if (_channel == null || _client?.ConnectionState != ConnectionState.Connected)
            {
                Console.WriteLine($"SessionSwitch event ignored: Channel is null or client not connected.");
                return;
            }

            try
            {
                if (e.Reason == SessionSwitchReason.SessionLock)
                {
                    _wasLocked = true;
                    Console.WriteLine("Session Locked.");
                    await _channel.SendMessageAsync($"🔒 Computer locked at {DateTime.Now:T}"); // T = Short time pattern
                }
                else if (e.Reason == SessionSwitchReason.SessionUnlock)
                {
                    _wasLocked = false;
                    Console.WriteLine("Session Unlocked.");
                    await _channel.SendMessageAsync($"🔓 Computer unlocked at {DateTime.Now:T}"); // T = Short time pattern
                }
                // You could potentially handle other reasons like ConsoleConnect/Disconnect if needed
            }
            catch (Exception ex)
            {
                 Console.WriteLine($"Error sending session switch notification: {ex.Message}");
                 // Maybe try resending later or log persistently?
            }
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

            // Setup Discord asynchronously AFTER the UI is initialized but before Application.Run()
            // We use Task.Run to avoid blocking the UI thread during initial connection.
            // We pass the loaded token and channelId.
            _ = Task.Run(() => program.SetupDiscord(_token!, _channelId)); // Use discard _ for fire-and-forget

            // Start the WinForms message loop for the tray icon
            Application.Run();
        }
    }
}