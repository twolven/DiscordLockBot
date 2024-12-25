using System;
using Discord;
using Discord.WebSocket;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using System.ServiceProcess;

namespace LockStatusService 
{
    public class LockService : ServiceBase
    {
        private DiscordSocketClient _client;
        private const string TOKEN = "YOUR_BOT_TOKEN";
        private const ulong CHANNEL_ID = 123456789;
        private bool _wasLocked = false;
        private IMessageChannel? _channel;

        [DllImport("user32.dll")]
        static extern bool LockWorkStation();

        public LockService()
        {
            ServiceName = "LockMonitor";
        }

        protected override void OnStart(string[] args)
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

            _client.LoginAsync(TokenType.Bot, TOKEN).GetAwaiter().GetResult();
            _client.StartAsync().GetAwaiter().GetResult();
        }

        private async Task Ready()
        {
            _channel = _client.GetChannel(CHANNEL_ID) as IMessageChannel;
            if (_channel != null)
            {
                await _channel.SendMessageAsync("🟢 Service started and monitoring lock status");
            }
        }

        private Task Log(LogMessage msg)
        {
            return Task.CompletedTask;
        }

        private async Task HandleCommand(SocketMessage message)
        {
            if (message.Channel.Id != CHANNEL_ID || message.Author.IsBot) return;

            string command = message.Content.ToLower();
            switch (command)
            {
                case "!locked":
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
                        "!locked or !status - Check lock status\n" +
                        "!lock - Lock the computer"
                    );
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