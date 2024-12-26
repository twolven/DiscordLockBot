@echo off
echo Building LockBot...
dotnet publish -c Release
if errorlevel 1 (
    echo Build failed!
    pause
    exit /b 1
)
echo Build completed successfully
pause