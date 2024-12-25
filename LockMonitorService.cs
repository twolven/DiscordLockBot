using System;
using Discord;
using Discord.WebSocket;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using System.ServiceProcess;
using System.Threading;

namespace LockStatusService 
{
    public class LockService : ServiceBase
    {
        private DiscordSocketClient? _client;
        private const string TOKEN = "YOUR_BOT_TOKEN";
        private const ulong CHANNEL_ID = 123456789;
        private bool _wasLocked = false;
        private IMessageChannel? _channel;
        private readonly ManualResetEvent _stopEvent = new ManualResetEvent(false);

        [DllImport("user32.dll")]
        static extern bool LockWorkStation();

        public LockService()
        {
            ServiceName = "LockMonitor";
            CanHandlePowerEvent = true;
            CanHandleSessionChangeEvent = true;
        }

        protected override void OnStart(string[] args)
        {
            // Start Discord connection in background thread
            Task.Run(async () => 
            {
                try 
                {
                    _client = new DiscordSocketClient(new DiscordSocketConfig
                    {
                        LogLevel = LogSeverity.Debug,
                        MessageCacheSize = 50,
                        GatewayIntents = GatewayIntents.AllUnprivileged | GatewayIntents.MessageContent
                    });
            
                    _client.MessageReceived += HandleCommand;
                    _client.Ready += Ready;

                    await _client.LoginAsync(TokenType.Bot, TOKEN);
                    await _client.StartAsync();

                    SystemEvents.SessionSwitch += SystemEvents_SessionSwitch;

                    // Keep the task alive
                    await Task.Run(() => _stopEvent.WaitOne());
                }
                catch (Exception ex) 
                {
                    // Log error but don't crash service
                    using (var eventLog = new System.Diagnostics.EventLog("Application"))
                    {
                        eventLog.Source = "LockMonitor";
                        eventLog.WriteEntry($"Service error: {ex.Message}", System.Diagnostics.EventLogEntryType.Error);
                    }
                }
            });
        }

        private async Task Ready()
        {
            _channel = _client?.GetChannel(CHANNEL_ID) as IMessageChannel;
            if (_channel != null)
            {
                await _channel.SendMessageAsync("🟢 Bot started and monitoring lock status");
            }
        }

        private async Task HandleCommand(SocketMessage message)
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
            }
        }

        private async void SystemEvents_SessionSwitch(object sender, SessionSwitchEventArgs e)
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

        protected override void OnStop()
        {
            _stopEvent.Set();  // Signal the background task to stop
            SystemEvents.SessionSwitch -= SystemEvents_SessionSwitch;
            _client?.StopAsync().GetAwaiter().GetResult();
            _client?.LogoutAsync().GetAwaiter().GetResult();
        }

        public static void Main()
        {
            ServiceBase.Run(new LockService());
        }
    }
}