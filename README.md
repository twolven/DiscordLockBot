# Lock Status Monitor (Formerly DiscordLockBot)

A lightweight Windows tray application that monitors your computer's lock status and reports it to a Discord channel. Get notifications when your PC locks/unlocks and optionally lock it remotely via Discord command.

## Features
- 🔒 Real-time lock/unlock notifications sent to Discord
- 💬 Discord commands to check status (`!status`) and lock PC (`!lock`)
- ⚙️ Easy configuration via `config.txt` file (no recompiling needed!)
- ✨ Custom system tray icon
- 🚀 Option to run automatically at Windows startup
- 🟢 Startup/shutdown status notifications in Discord
- 🔄 Minimal resource usage

## Prerequisites

**For Running the Release Executable:**
*   Windows 10 or 11
*   A Discord Bot Token and Channel ID (see Discord Setup below)

**For Compiling from Source (Optional):**
*   [.NET 8.0 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) or later (Developed with 8.0, compatible with newer versions)
*   Windows 10 or 11

## Installation

### 1. Discord Setup (Required for both methods)
1. Create a new Discord application at the [Discord Developer Portal](https://discord.com/developers/applications).
2. Go to the "Bot" tab and click "Add Bot".
3. **Enable the `MESSAGE CONTENT INTENT`** under the "Privileged Gateway Intents" section on the Bot tab. This is required for the bot to read commands like `!status`.
4. Copy the bot **Token** (you'll need this for `config.txt`). Click "Reset Token" if you don't see it. Keep this token secure!
5. Go to the "OAuth2" tab -> "URL Generator". Select the `bot` scope.
6. In the "Bot Permissions" section that appears, select `Send Messages` and `Read Message History`.
7. Copy the generated URL and paste it into your browser. Invite the bot to your desired server.
8. Get the **Channel ID** for the specific channel where you want the bot to post messages: Enable Developer Mode in Discord User Settings (Advanced section), then right-click the channel name and select "Copy Channel ID". You'll need this numeric ID for `config.txt`.

### 2. Using the Release Executable (Recommended)
1. Go to the [Releases page](https://github.com/twolven/DiscordLockBot/releases) of this repository.
2. Download the `.zip` file from the latest release assets.
3. Extract the contents of the `.zip` file into a dedicated folder on your computer.
4. **Find the `config.txt` file** included within the extracted files.
5. Open `config.txt` with a text editor (like Notepad).
6. Replace `YOUR_DISCORD_BOT_TOKEN_HERE` with your actual Discord Bot Token obtained in Step 1.4.
7. Replace `YOUR_DISCORD_CHANNEL_ID_HERE` with the numeric Channel ID obtained in Step 1.8.
8. **Save** and close the `config.txt` file.
9. Run the `lockbot.exe` (or similar name) file. It should now connect successfully and appear in your system tray.

### 3. Compiling from Source (Optional)
```bash
# Clone the repository
git clone https://github.com/twolven/DiscordLockBot
cd DiscordLockBot

# Build the application using the build script or dotnet command
# Option 1: Run the build script
./build.bat
# Option 2: Use dotnet CLI directly
dotnet publish -c Release

# Navigate to the publish directory (e.g., bin\Release\net8.0-windows\publish\)
# Find the config.txt template file (you might need to copy it from the project source
# into the publish directory manually if the build doesn't include it).
# Edit config.txt with your Token and Channel ID as described in steps 5-8
# of the "Using the Release Executable" section above.
# Run the executable after configuring config.txt.
```

## Usage
### Running the Application
1. Once configured (via `config.txt`), run the `lockbot.exe`.
2. The application minimizes to the system tray (look for the lock/? icon).
3. Right-click the tray icon for options:
   - **Show Status:** View current connection state, lock status, and configuration info.
   - **Run at Startup:** Toggle whether the application starts automatically when you log into Windows.
   - **Exit:** Close the application (sends a shutdown message to Discord if connected).

### Discord Commands
(Send these in the channel specified in `config.txt`)
- `!status` - Check the current lock status of the monitored computer.
- `!lock` - Attempt to lock the monitored computer remotely.
- `!help` - Show available commands.

### Automatic Notifications
(Sent to the configured Discord channel)
- 🔒 Computer locked notification
- 🔓 Computer unlocked notification
- 🟢 Application start notification
- 🔴 Application shutdown notification (when exited gracefully)

## Troubleshooting
### Bot not responding / Application won't connect
1. **Double-check `config.txt`:** Ensure the `TOKEN` and `CHANNEL_ID` are correct and there are no extra spaces. Make sure you saved the file after editing. Did you remove the placeholder text entirely?
2. **Check Discord Bot Permissions:** Verify the bot was invited with `Send Messages` and `Read Message History` permissions in the correct channel.
3. **Check Privileged Intents:** Ensure the `MESSAGE CONTENT INTENT` is enabled for your bot in the Discord Developer Portal.
4. **Channel ID:** Make sure the `CHANNEL_ID` in `config.txt` is for the specific channel you want the bot to use and where you are typing commands.
5. **Internet Connection:** Ensure the computer running the application has internet access.

### Application crashes immediately
1. Check `config.txt` first, as invalid values can cause startup issues. Ensure you replaced the placeholder text correctly.
2. If compiling from source, ensure the correct .NET SDK is installed and the build completed without errors.
3. Try running the `.exe` from a command prompt (`cmd` or PowerShell) - it might print error messages to the console before exiting.

### Startup issues ("Run at Startup")
If the application doesn't start automatically with Windows after enabling the option:
1. Ensure the application runs correctly when started manually *after* `config.txt` is properly filled out.
2. Try toggling the "Run at Startup" option off and on again via the tray menu. Wait a few seconds between toggles.
3. Check Windows Task Manager -> Startup tab to see if "LockStatusMonitor" (or similar) is listed and enabled.

## Contributing
Pull requests are welcome. For major changes, please open an issue first to discuss what you would like to change.

## License
MIT
