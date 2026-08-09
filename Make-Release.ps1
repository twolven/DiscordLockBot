<#
.SYNOPSIS
    Builds a release zip for Lock Status Monitor from a clean staging directory.

.DESCRIPTION
    Never zips a build directory in place. Build folders accumulate local
    working files, so this stages the publish output into a fresh directory,
    resets every config.txt to the repo's placeholder template, and verifies
    the staged tree before archiving. It aborts rather than producing an
    archive if the check fails.

.EXAMPLE
    .\Make-Release.ps1 -Version 1.4
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Version,

    [string]$OutputDir = "$PSScriptRoot\release"
)

$ErrorActionPreference = 'Stop'

$publishDir = Join-Path $PSScriptRoot 'bin\Release\net9.0-windows\publish'
$template   = Join-Path $PSScriptRoot 'config.txt'
$stage      = Join-Path $OutputDir "stage-$Version"
$zipPath    = Join-Path $OutputDir "LockStatusMonitor-v$Version.zip"

Write-Host 'Building...' -ForegroundColor Cyan
dotnet publish -c Release
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed.' }

if (-not (Test-Path $template)) { throw "Missing placeholder template: $template" }
if ((Get-Content $template | Select-String '^TOKEN=YOUR_DISCORD_BOT_TOKEN_HERE').Count -eq 0) {
    throw "The repo's config.txt is not the placeholder template - refusing to package it."
}

# Stage into a clean directory. Only the publish output ships; never the parent
# build folder, which would nest a second copy inside the archive.
if (Test-Path $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
New-Item -ItemType Directory -Force -Path $stage | Out-Null
Copy-Item -Path (Join-Path $publishDir '*') -Destination $stage -Recurse -Force

# Overwrite every config.txt in the staged tree, at any depth.
$configs = Get-ChildItem -LiteralPath $stage -Recurse -Filter 'config.txt'
foreach ($c in $configs) {
    Copy-Item -LiteralPath $template -Destination $c.FullName -Force
    Write-Host "  sanitized $($c.FullName.Replace($stage,''))" -ForegroundColor DarkGray
}

# Fail closed: scan the staged tree for anything token-shaped before zipping.
$pattern = '[MNO][A-Za-z0-9_-]{22,}\.[A-Za-z0-9_-]{6}\.[A-Za-z0-9_-]{27,}'
$leaks = Get-ChildItem -LiteralPath $stage -Recurse -File -Include *.txt, *.json, *.ini, *.cfg -ErrorAction SilentlyContinue |
         Select-String -Pattern $pattern
if ($leaks) {
    $leaks | ForEach-Object { Write-Host "LEAK: $($_.Path) line $($_.LineNumber)" -ForegroundColor Red }
    throw 'Credential-shaped string found in the staged release. Aborting.'
}

if (Test-Path $zipPath) { Remove-Item -LiteralPath $zipPath -Force }
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zipPath -Force

Write-Host ''
Write-Host "Release built: $zipPath" -ForegroundColor Green
Write-Host ("  size: {0} MB" -f [math]::Round((Get-Item $zipPath).Length / 1MB, 2))
Write-Host ''
Write-Host 'Before publishing, confirm the active GitHub account is the repo owner:' -ForegroundColor Yellow
Write-Host '  gh api user --jq .login        # expect: twolven'
Write-Host '  gh auth switch --user twolven  # if it is not'
Write-Host ''
Write-Host "Then: gh release create $Version `"$zipPath`" --title `"v$Version`" --notes `"...`""
