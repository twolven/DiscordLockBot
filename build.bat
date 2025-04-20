@echo off
echo =====================================
echo Building Lock Status Monitor
echo =====================================

REM Configuration is now handled by config.txt at runtime.
REM The build script no longer checks source code for placeholders.

echo Building the application...
REM Use the correct project/solution file if needed, otherwise dotnet publish often finds it.
dotnet publish -c Release

REM Check if the build command failed
if errorlevel 1 (
    echo.
    echo =====================================
    echo          BUILD FAILED!
    echo =====================================
    echo Check the output above for errors.
    pause
    exit /b 1
)

echo.
echo =====================================
echo      BUILD COMPLETED SUCCESSFULLY!
echo =====================================
echo The application executable can be found in a subfolder within:
echo bin\Release\ (e.g., bin\Release\netX.Y-windows\publish\)
echo.
echo =====================================
echo          IMPORTANT REMINDER
echo =====================================
echo Before running the application, you MUST create a 'config.txt'
echo file in the same directory as the .exe file.
echo.
echo This file needs to contain your:
echo   TOKEN=YOUR_DISCORD_BOT_TOKEN_HERE
echo   CHANNEL_ID=YOUR_DISCORD_CHANNEL_ID_HERE
echo.
echo (Replace the placeholders with your actual token and channel ID)
echo See the README.md for more details.
echo =====================================

pause
exit /b 0