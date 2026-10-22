# Compatibility entry point for developers; creates distributable packages, never replaces a running app.
& (Join-Path $PSScriptRoot 'build_release.ps1') @args
if (-not $?) { exit 1 }
