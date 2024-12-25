# DiscordLockBot

A lightweight Windows service that monitors your computer's lock status and reports to Discord. Get notifications when your PC locks/unlocks and control locking remotely.

## Features
- 🔒 Real-time lock/unlock notifications
- 💬 Discord commands to check status and control PC
- 🚀 Runs as Windows service
- 🔄 Auto-restarts if crashed
- 🟢 Startup notifications

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

# Install dependencies
dotnet add package Discord.Net
dotnet add package Microsoft.Win32.SystemEvents
dotnet add package System.ServiceProcess.ServiceController

# Update configuration
# Edit LockMonitorService.cs and replace:
# - YOUR_BOT_TOKEN with Discord bot token
# - CHANNEL_ID with Discord channel ID

# Build service
dotnet publish -c Release -r win-x64 --self-contained
```

### 3. Service Installation
Run PowerShell as Administrator (Right-Click Windows on your Taskbar - Click Powershell(Administrator) or Terminal(Administrator):
```powershell
# Install service
New-Service -Name "LockMonitor" -BinaryPathName "C:\path\to\your\published\exe"

# Configure auto-start
sc.exe config LockMonitor start= auto

# Configure auto-restart
sc.exe failure LockMonitor reset= 0 actions= restart/60000/restart/60000/restart/60000

# Start service
Start-Service LockMonitor
```

## Usage
### Commands
- `!status` or `!locked` - Check current lock status
- `!lock` - Lock computer remotely
- `!help` - Show available commands

### Automatic Notifications
- 🔒 Computer locked notification
- 🔓 Computer unlocked notification
- 🟢 Service start notification

## Troubleshooting
### Service won't start
Check Windows Event Viewer → Windows Logs → Application for error details

### Discord bot not responding
1. Verify bot token and channel ID
2. Check bot has correct permissions
3. Ensure MESSAGE CONTENT INTENT is enabled

### Service keeps crashing
1. Stop service: `Stop-Service LockMonitor`
2. Delete service: `sc.exe delete LockMonitor`
3. Reinstall following installation steps

## Contributing
Pull requests welcome. For major changes, open issue first.

## License
MIT
