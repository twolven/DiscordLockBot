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
- 🖥️ **Display Recovery** - Automatically restore window positions and desktop icons after unlock (great for OLED monitors that scramble layouts after sleep!)
- 🌙 **Display Standby Enforcement** - Close the programs that hold a DISPLAY power request (Moonlight, slideshow/presentation windows, media players) after the PC has been locked for a while, so an OLED never sits lit for hours

## Prerequisites

**For Running the Release Executable:**
*   Windows 10 or 11
*   A Discord Bot Token and Channel ID (see Discord Setup below)

**For Compiling from Source (Optional):**
*   [.NET 9.0 SDK](https://dotnet.microsoft.com/download/dotnet/9.0) or later
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

# Navigate to the publish directory (e.g., bin\Release\net9.0-windows\publish\)
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

### Autostart with elevation (required for `DISPLAY_KILL_AUTO`)

"Run at Startup" uses the HKCU Run key, which cannot launch the app **elevated**.
`DISPLAY_KILL_AUTO` closes another process's window and needs elevation, so that setup
requires a "run with highest privileges" logon scheduled task instead.

**Do not create that task by hand with `schtasks /Create`.** Use the included script from
an elevated PowerShell prompt:

```powershell
.\Install-ScheduledTask.ps1 -ExePath 'C:\Tools\LockStatusMonitor\lockbot.exe'
Start-ScheduledTask -TaskName 'LockStatusMonitor'
```

> ⚠️ **Why this matters.** Both `schtasks /Create` and `Register-ScheduledTask` default
> `ExecutionTimeLimit` to **72 hours**. Task Scheduler then hard-kills `lockbot.exe`
> exactly 3 days after it starts. It's a kill, not a crash — no exception handler runs,
> nothing lands in `lockbot.log`, and no shutdown message reaches Discord. The app just
> vanishes and lock notifications silently stop. The script sets the limit to unlimited,
> clears the battery settings (a UPS blip otherwise stops the task the same silent way),
> and adds a 15-minute backstop trigger that relaunches the app if it is ever down.

### Discord Commands
(Send these in the channel specified in `config.txt`)
- `!status` - Check the current lock status of the monitored computer.
- `!lock` - Attempt to lock the monitored computer remotely.
- `!display` - Show display-standby settings and what is currently keeping the monitors awake.
- `!sweep` - Close display blockers now, skipping the wait (still requires the PC to be locked).
- `!help` - Show available commands.

### Automatic Notifications
(Sent to the configured Discord channel)
- 🔒 Computer locked notification
- 🔓 Computer unlocked notification
- 🟢 Application start notification
- 🔴 Application shutdown notification (when exited gracefully)
- 🖥️ Display Recovery completion notification (when enabled)

## Display Recovery Feature

If you have an OLED monitor (or any display that causes window/icon scrambling after waking from sleep), the Display Recovery feature can automatically fix this.

### How It Works
1. **On Lock**: The application captures the position of all visible windows
2. **On Unlock**: After a configurable delay (for monitor handshake):
   - Restores desktop icons using [DesktopOK](https://www.softwareok.com/?seite=Freeware/DesktopOK) (optional)
   - Restores all window positions to their pre-lock state

### Configuration
Add these optional settings to your `config.txt`:

```ini
# --- Display Recovery Settings (Optional) ---

# Path to DesktopOK folder (contains exe and .dok files)
# Download from: https://www.softwareok.com/?seite=Freeware/DesktopOK
# Example: C:\DesktopOK
DESKTOPOK_PATH=C:\DesktopOK

# Delay in milliseconds before restoring windows after unlock (default: 5000)
# Increase if your monitor takes longer to wake up from sleep
MONITOR_DELAY_MS=5000
```

### DesktopOK Setup (Optional)
1. Download [DesktopOK](https://www.softwareok.com/?seite=Freeware/DesktopOK) (portable version recommended)
2. Extract to a folder (e.g., `C:\DesktopOK`)
3. Run DesktopOK and arrange your desktop icons as desired
4. Save your icon layout (it creates a `.dok` file in the same folder)
5. Set `DESKTOPOK_PATH` in `config.txt` to your DesktopOK folder
6. The application will automatically find the most recent `.dok` file and use it

### Notes
- Window position restoration works independently of DesktopOK
- If `DESKTOPOK_PATH` is not configured, only window positions are restored
- The delay allows your monitor to complete its handshake before restoration begins

## Display Standby Enforcement

Windows will not put a monitor into standby while any process holds a DISPLAY power
request (`SetThreadExecutionState(ES_DISPLAY_REQUIRED)`). Moonlight, a PowerPoint
slideshow, a media player, or a forgotten "presenting" window will all keep an OLED
lit indefinitely — which is exactly the situation you do not want to leave running
while you are away for a week.

When enabled, the app waits until the PC has been **continuously locked** for a
configurable number of minutes, then closes the offending programs and tells the
monitors to power off.

**It only ever acts while the PC is locked.** An unlocked session means you are sitting
at the machine, so nothing is closed — every trigger, including the manual `!sweep`
command and the tray item, is gated on the live lock state.

### How It Works
1. **On lock**: a sweep is armed. Unlocking at any point during the wait cancels it.
2. **After the grace period** (default 30 minutes of unbroken lock):
   - Every process in `DISPLAY_KILL_PROCESSES` is closed — politely first
     (`CloseMainWindow`), then force-killed if it ignores the request and
     `DISPLAY_KILL_FORCE=true`.
   - If `DISPLAY_KILL_AUTO=true` **and the app is running elevated**, `powercfg /requests`
     is parsed and anything holding a DISPLAY request is closed too.
   - `DISPLAY_FORCE_OFF` then broadcasts a monitor power-off — the safety net for a
     blocker that could not be closed.
3. A summary is posted to Discord: what was closed, what refused, and what is still
   holding the display awake.

### Configuration
```ini
# --- Display Standby Enforcement (Optional) ---

# Master switch. Everything below is ignored unless this is true.
DISPLAY_STANDBY_ENFORCE=false

# Comma-separated process names to close (".exe" optional). Works without admin.
DISPLAY_KILL_PROCESSES=Moonlight,POWERPNT,vlc

# Also auto-discover blockers via 'powercfg /requests' and close them.
# REQUIRES the app to run elevated; skipped (with a warning) otherwise.
DISPLAY_KILL_AUTO=false

# Names never closed, on top of the built-in system-process protection list.
DISPLAY_KILL_EXCLUDE=

# Minutes the PC must stay CONTINUOUSLY locked before anything is closed.
DISPLAY_KILL_DELAY_MINUTES=30

# Force-kill a process that ignores the polite close request.
# Set false if you would rather keep unsaved work than guarantee standby.
DISPLAY_KILL_FORCE=true

# After sweeping, tell the monitors to power off immediately.
DISPLAY_FORCE_OFF=true
```

### Safety Notes
- Core Windows processes (`explorer`, `dwm`, `winlogon`, `csrss`, …) and the app
  itself are on a hard-coded protection list and are never closed, even if
  auto-discovery names them. They are reported instead.
- Processes in other sessions (services, other users) are never touched.
- If `powercfg /requests` cannot be read — the usual case, since it needs elevation —
  auto-discovery contributes **nothing**. "Unknown" is never treated as "nothing is
  blocking", so a failed read can never green-light a kill.
- `DISPLAY_KILL_FORCE=true` can discard unsaved work in whatever it closes. That is
  the intended trade for burn-in protection; set it to `false` to reverse the priority.
- To use `DISPLAY_KILL_AUTO`, launch the app elevated (a scheduled task with
  "Run with highest privileges" — the `Run` registry key cannot start elevated).

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

### Bot silently stops after ~3 days (scheduled-task autostart)
If notifications just stop with no error, no crash message in `lockbot.log`, and no 🔴
shutdown message in Discord — and the log's last line is ordinary activity — Task
Scheduler killed the app at its 72-hour `ExecutionTimeLimit`. Confirm it:

```powershell
(Get-ScheduledTaskInfo -TaskName 'LockStatusMonitor').LastTaskResult
# 267014  (0x41306, SCHED_S_TASK_TERMINATED)  = it was killed, not crashed

(Get-ScheduledTask -TaskName 'LockStatusMonitor').Settings.ExecutionTimeLimit
# PT72H = the bug.  PT0S = correct (unlimited).
```

Fix by re-registering the task with `Install-ScheduledTask.ps1` (elevated), which sets an
unlimited time limit and adds a backstop trigger. See
[Autostart with elevation](#autostart-with-elevation-required-for-display_kill_auto).

Note that a 72h kill leaves the log ending mid-normal-operation, which reads exactly like
a hang — check `LastTaskResult` before debugging the app itself.

### Startup issues ("Run at Startup")
If the application doesn't start automatically with Windows after enabling the option:
1. Ensure the application runs correctly when started manually *after* `config.txt` is properly filled out.
2. Try toggling the "Run at Startup" option off and on again via the tray menu. Wait a few seconds between toggles.
3. Check Windows Task Manager -> Startup tab to see if "LockStatusMonitor" (or similar) is listed and enabled.

## Contributing
Pull requests are welcome. For major changes, please open an issue first to discuss what you would like to change.

## License
MIT
