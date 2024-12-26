@echo off
echo ===============================
echo LockBot Configuration Checker
echo ===============================

set ERROR=0

findstr /C:"private const string TOKEN = \"YOUR_BOT_TOKEN\";" DiscordLockBot.cs >nul
if not errorlevel 1 (
    echo [X] Discord bot token not configured
    echo     Edit DiscordLockBot.cs and replace YOUR_BOT_TOKEN with your bot token
    echo     Get one from https://discord.com/developers/applications
    set ERROR=1
) else (
    echo [✓] Discord token configured
)

findstr /C:"private const ulong CHANNEL_ID = 123456789;" DiscordLockBot.cs >nul
if not errorlevel 1 (
    echo [X] Discord channel ID not configured
    echo     Edit DiscordLockBot.cs and replace 123456789 with your channel ID
    echo     Enable Developer Mode in Discord to copy channel IDs
    set ERROR=1
) else (
    echo [✓] Channel ID configured
)

echo ===============================
if %ERROR%==1 (
    echo Build cancelled: Please fix configuration errors
    pause
    exit /b 1
)

echo Building LockBot...
dotnet publish -c Release

if errorlevel 1 (
    echo Build failed!
    pause
    exit /b 1
)

echo ===============================
echo Build completed successfully!
echo The executable can be found in:
echo bin\Release\net9.0-windows\publish\
echo ===============================
pause