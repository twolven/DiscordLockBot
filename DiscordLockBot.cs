using System;
using System.Drawing;
using System.Windows.Forms;
using Discord;
using Discord.WebSocket;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace DiscordLockBot 
{
    public class Program
    {
        private NotifyIcon? trayIcon;
        private ContextMenuStrip? trayMenu;
        private static DiscordSocketClient? _client;
        private const string TOKEN = "YOUR_BOT_TOKEN"; // Your Discord Bot Token
        private const ulong CHANNEL_ID = 123456789; // Your channel ID
        private static bool _wasLocked = false;
        private static IMessageChannel? _channel;
        private const string StartupKey = "SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Run";
        private const string StartupValue = "DiscordLockBot";

        [DllImport("user32.dll")]
        static extern bool LockWorkStation();

        public Program()
        {
            InitializeComponents();
            SetupDiscord().GetAwaiter().GetResult();
        }

        private void InitializeComponents()
        {
            trayIcon = new NotifyIcon();
            trayMenu = new ContextMenuStrip();

            trayMenu.Items.Add("Show Status", null, ShowStatus);
            trayMenu.Items.Add("Run at Startup", null, ToggleStartup);
            trayMenu.Items.Add("-");
            trayMenu.Items.Add("Exit", null, Exit);

            trayIcon.Icon = SystemIcons.Application;
            trayIcon.Text = "Discord Lock Bot";
            trayIcon.ContextMenuStrip = trayMenu;
            trayIcon.Visible = true;

            ((ToolStripMenuItem)trayMenu.Items[1]).Checked = IsStartupEnabled();
        }

        private async Task SetupDiscord()
        {
            _client = new DiscordSocketClient(new DiscordSocketConfig
            {
                LogLevel = LogSeverity.Debug,
                MessageCacheSize = 50,
                GatewayIntents = GatewayIntents.AllUnprivileged | GatewayIntents.MessageContent
            });
            
            _client.Log += Log;
            _client.MessageReceived += HandleCommand;
            _client.Ready += Ready;

            SystemEvents.SessionSwitch += SystemEvents_SessionSwitch;

            await _client.LoginAsync(TokenType.Bot, TOKEN);
            await _client.StartAsync();
        }

        private bool IsStartupEnabled()
        {
            using (RegistryKey? key = Registry.CurrentUser.OpenSubKey(StartupKey))
            {
                return key?.GetValue(StartupValue) != null;
            }
        }

        private void ToggleStartup(object? sender, EventArgs e)
        {
            var menuItem = sender as ToolStripMenuItem;
            if (menuItem == null) return;

            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(StartupKey, true))
                {
                    if (key == null) return;

                    if (IsStartupEnabled())
                    {
                        key.DeleteValue(StartupValue, false);
                        menuItem.Checked = false;
                    }
                    else
                    {
                        string appPath = Application.ExecutablePath;
                        key.SetValue(StartupValue, appPath);
                        menuItem.Checked = true;
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to update startup settings: {ex.Message}", "Error", 
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void ShowStatus(object? sender, EventArgs e)
        {
            MessageBox.Show($"Bot Status: {(_client?.ConnectionState == ConnectionState.Connected ? "Connected" : "Disconnected")}\n" +
                          $"Lock Status: {(_wasLocked ? "Locked" : "Unlocked")}\n" +
                          $"Last Updated: {DateTime.Now}",
                          "Lock Status Monitor",
                          MessageBoxButtons.OK,
                          MessageBoxIcon.Information);
        }

        private void Exit(object? sender, EventArgs e)
        {
            if (_channel != null)
            {
                _channel.SendMessageAsync("🔴 Bot shutting down...").GetAwaiter().GetResult();
            }
            
            if (trayIcon != null)
            {
                trayIcon.Visible = false;
                trayIcon.Dispose();
            }
            Application.Exit();
        }

        private static async Task Ready()
        {
            _channel = _client?.GetChannel(CHANNEL_ID) as IMessageChannel;
            if (_channel != null)
            {
                await _channel.SendMessageAsync("🟢 Bot started and monitoring lock status");
            }
        }

        private static Task Log(LogMessage msg)
        {
            Console.WriteLine(msg.ToString());
            return Task.CompletedTask;
        }

        private static async Task HandleCommand(SocketMessage message)
        {
            if (message.Channel.Id != CHANNEL_ID || message.Author.IsBot) return;

            string command = message.Content.ToLower();
            switch (command)
            {
                case "!status":
                    await message.Channel.SendMessageAsync($"🔒 Status: {(_wasLocked ? "Locked" : "Unlocked")} | Last checked: {DateTime.Now}");
                    break;

                case "!lock":
                    if (LockWorkStation())
                    {
                        await message.Channel.SendMessageAsync("🔒 Locking computer...");
                        _wasLocked = true;
                    }
                    else
                    {
                        await message.Channel.SendMessageAsync("❌ Failed to lock computer");
                    }
                    break;

                case "!help":
                    await message.Channel.SendMessageAsync(
                        "📋 Available commands:\n" +
                        "!status - Check lock status\n" +
                        "!lock - Lock the computer"
                    );
                    break;
            }
        }

        private static async void SystemEvents_SessionSwitch(object sender, SessionSwitchEventArgs e)
        {
            if (_channel == null) return;

            if (e.Reason == SessionSwitchReason.SessionLock)
            {
                _wasLocked = true;
                await _channel.SendMessageAsync($"🔒 Computer locked at {DateTime.Now}");
            }
            else if (e.Reason == SessionSwitchReason.SessionUnlock)
            {
                _wasLocked = false;
                await _channel.SendMessageAsync($"🔓 Computer unlocked at {DateTime.Now}");
            }
        }

        [STAThread]
        static void Main()
        {
            Application.EnableVisualStyles();
            var program = new Program();
            Application.Run();
        }
    }
}