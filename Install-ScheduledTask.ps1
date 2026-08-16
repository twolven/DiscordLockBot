<#
.SYNOPSIS
    Installs LockStatusMonitor as an elevated logon scheduled task, configured so it
    actually stays running.

.DESCRIPTION
    The tray menu's "Run at Startup" uses the HKCU Run key, which cannot launch the app
    elevated. DISPLAY_KILL_AUTO needs elevation to close another process's window, so
    that setup requires a "run with highest privileges" logon task instead.

    Create that task by hand and you inherit a silent 3-day time bomb:

      * schtasks /Create and Register-ScheduledTask both default ExecutionTimeLimit to
        72 hours (PT72H). Task Scheduler then HARD-KILLS lockbot.exe exactly 72h after
        it launches. Because it is a kill and not a crash, no exception handler runs,
        nothing is written to lockbot.log, and no shutdown message reaches Discord.
        The log simply stops mid-normal-operation. The tray icon disappears and lock
        notifications stop arriving, with no error anywhere to explain it.
        Symptom: `(Get-ScheduledTaskInfo LockStatusMonitor).LastTaskResult` = 267014
        (0x41306, SCHED_S_TASK_TERMINATED).

      * StopIfGoingOnBatteries defaults to $true. On a desktop behind a UPS, a brief
        power blip reads as "on battery" and stops the task the same silent way.

      * An At-Logon-only trigger never re-fires on a machine you don't log off of, so
        nothing recovers from either case until the next reboot.

      * The obvious way to build the backstop trigger - copying .Repetition off a
        `New-ScheduledTaskTrigger -Once` object - drags along StopAtDurationEnd = $true.
        That means "stop all running tasks at the end of the repetition duration", so a
        1-day duration becomes a fresh 24-hour kill timer: the app is terminated once a
        night and relaunched by the next repetition tick. Same silent-kill signature as
        the 72h case, just faster.

    This script sets ExecutionTimeLimit to unlimited, clears the battery settings, adds
    restart-on-failure, and adds a 15-minute backstop trigger that repeats indefinitely
    and never stops a healthy instance. The backstop is free while the app is healthy:
    MultipleInstances = IgnoreNew means the scheduler will not launch a second instance
    while the task is running, and lockbot's own single-instance mutex catches anything
    that slips past. It only ever starts the app when it is down.

.PARAMETER ExePath
    Full path to lockbot.exe. Defaults to lockbot.exe beside this script.

.EXAMPLE
    # From an ELEVATED PowerShell prompt:
    .\Install-ScheduledTask.ps1 -ExePath 'C:\Tools\LockStatusMonitor\lockbot.exe'
#>
[CmdletBinding()]
param(
    [string]$ExePath = (Join-Path $PSScriptRoot 'lockbot.exe'),
    [string]$TaskName = 'LockStatusMonitor'
)

$ErrorActionPreference = 'Stop'

$isAdmin = ([Security.Principal.WindowsPrincipal] `
    [Security.Principal.WindowsIdentity]::GetCurrent()
    ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    throw "Run this from an elevated PowerShell prompt. Registering a highest-privileges task fails with 'Access is denied' otherwise."
}

if (-not (Test-Path $ExePath)) {
    throw "lockbot.exe not found at '$ExePath'. Pass -ExePath with the full path to your install."
}
$ExePath = (Resolve-Path $ExePath).Path

$action = New-ScheduledTaskAction -Execute $ExePath -WorkingDirectory (Split-Path $ExePath -Parent)

$principal = New-ScheduledTaskPrincipal `
    -UserId "$env:USERDOMAIN\$env:USERNAME" `
    -LogonType Interactive `
    -RunLevel Highest

# ExecutionTimeLimit = Zero is the load-bearing line. Without it the app dies at 72h.
$settings = New-ScheduledTaskSettingsSet `
    -AllowStartIfOnBatteries `
    -DontStopIfGoingOnBatteries `
    -ExecutionTimeLimit ([TimeSpan]::Zero) `
    -MultipleInstances IgnoreNew `
    -RestartCount 3 `
    -RestartInterval (New-TimeSpan -Minutes 1) `
    -StartWhenAvailable

$logonTrigger = New-ScheduledTaskTrigger -AtLogOn -User "$env:USERDOMAIN\$env:USERNAME"

# Backstop: re-launch within 15 minutes if the task is ever not running.
# The two overrides below are load-bearing. The repetition object comes back with
# StopAtDurationEnd = $true, which turns the duration into a kill timer for the RUNNING
# instance; a null Duration is what Task Scheduler renders as "Indefinitely".
$backstop = New-ScheduledTaskTrigger -Daily -At '3:00AM'
$repetition = (New-ScheduledTaskTrigger -Once -At '3:00AM' `
    -RepetitionInterval (New-TimeSpan -Minutes 15) `
    -RepetitionDuration (New-TimeSpan -Days 1)).Repetition
$repetition.StopAtDurationEnd = $false
$repetition.Duration = $null
$backstop.Repetition = $repetition

Register-ScheduledTask -TaskName $TaskName `
    -Action $action `
    -Principal $principal `
    -Settings $settings `
    -Trigger @($logonTrigger, $backstop) `
    -Force | Out-Null

Write-Host "Registered scheduled task '$TaskName' -> $ExePath"
Write-Host ""

$t = Get-ScheduledTask -TaskName $TaskName
$t.Settings |
    Select-Object ExecutionTimeLimit, StopIfGoingOnBatteries, DisallowStartIfOnBatteries,
                  RestartCount, RestartInterval, MultipleInstances |
    Format-List | Out-String | Write-Host

Write-Host "Triggers:"
$t.Triggers | ForEach-Object {
    "  {0}  start={1}  every={2}  for={3}" -f $_.CimClass.CimClassName,
        $_.StartBoundary, $_.Repetition.Interval,
        $(if ($_.Repetition.Duration) { $_.Repetition.Duration } else { 'Indefinitely' }) | Write-Host
}

# Read the registered XML back rather than trusting the objects above: both kill timers
# are settings that look harmless in the summary view and only bite days later.
[xml]$xml = Export-ScheduledTask -TaskName $TaskName
$repXml = $xml.Task.Triggers.CalendarTrigger.Repetition
$problems = @()
if ($xml.Task.Settings.ExecutionTimeLimit -ne 'PT0S') {
    $problems += "ExecutionTimeLimit is $($xml.Task.Settings.ExecutionTimeLimit), not PT0S - the app WILL be killed when it expires."
}
if ($repXml.StopAtDurationEnd -eq 'true' -and $repXml.Duration) {
    $problems += "Backstop has StopAtDurationEnd=true with Duration=$($repXml.Duration) - the app WILL be killed at the end of every repetition window."
}

Write-Host ""
if ($problems) {
    $problems | ForEach-Object { Write-Host "PROBLEM: $_" -ForegroundColor Red }
    throw 'The registered task still contains a kill timer.'
}
Write-Host "Verified: ExecutionTimeLimit=PT0S, backstop repeats indefinitely and stops nothing." -ForegroundColor Green
Write-Host "Start it now with:  Start-ScheduledTask -TaskName '$TaskName'"
