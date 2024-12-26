# DiscordLockBot

A lightweight Windows tray application that monitors your computer's lock status and reports to Discord. Get notifications when your PC locks/unlocks and control locking remotely.

## Features
- 🔒 Real-time lock/unlock notifications
- 💬 Discord commands to check status and control PC
- 🎯 System tray integration
- 🚀 Startup support
- 🔄 Minimal resource usage
- 🟢 Status notifications

## Prerequisites
1. [.NET SDK 9.0](https://dotnet.microsoft.com/download)
2. Discord Bot Token ([Create one here](https://discord.com/developers/applications))
3. Windows 10/11

## Installation

### 1. Discord Setup
1. Create new Discord application at [Discord Developer Portal](https://discord.com/developers/applications)
2. Add bot to your application
3. Enable MESSAGE CONTENT INTENT under Bot settings
4. Copy bot token
5. Invite bot to your server using OAuth2 URL Generator (select bot scope + Send Messages and Read Message History permissions)
6. Get channel ID (Enable Developer Mode → Right click channel → Copy ID)

### 2. Code Setup
```bash
# Clone repository
git clone https://github.com/twolven/DiscordLockBot
cd DiscordLockBot

# Update configuration
# Edit Program.cs and replace:
# - TOKEN with your Discord bot token
# - CHANNEL_ID with your Discord channel ID

# Build application
# Run build.bat or use command:
dotnet publish -c Release
```

## Usage
### Running the Application
1. Run the compiled executable
2. The application will minimize to system tray
3. Right-click the tray icon for options:
   - Show Status: View current connection and lock status
   - Run at Startup: Toggle automatic startup
   - Exit: Close the application

### Discord Commands
- `!status` - Check current lock status
- `!lock` - Lock computer remotely
- `!help` - Show available commands

### Automatic Notifications
- 🔒 Computer locked notification
- 🔓 Computer unlocked notification
- 🟢 Application start notification
- 🔴 Application shutdown notification

## Troubleshooting
### Bot not responding
1. Verify bot token and channel ID
2. Check bot has correct permissions
3. Ensure MESSAGE CONTENT INTENT is enabled

### Application crashes on startup
1. Check Discord bot token and channel ID are correct
2. Verify .NET 9.0 is installed
3. Run application from command line to see error messages

### Startup issues
If the application won't start with Windows:
1. Right-click tray icon and uncheck "Run at Startup"
2. Wait a few seconds
3. Check "Run at Startup" again

## Contributing
Pull requests welcome. For major changes, open issue first.

## License
MIT