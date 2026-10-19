<#
.SYNOPSIS
  Test A1: Does an unmodified iPhone browse for _airdrop._tcp.local over the ordinary LAN?

.DESCRIPTION
  Listens passively on the Ethernet link while you open the AirDrop sheet on the iPhone.
  Sends nothing (except one optional DNS-SD enumeration query) and changes no settings.

  Read the result as:
    exit 0 - the iPhone DOES browse for AirDrop over this LAN. Path A stays open.
    exit 1 - no foreign _airdrop._tcp query was seen in the window. Path A is very likely
             closed, but re-run once before concluding: a single miss can be timing.

.PARAMETER Seconds
  How long to listen. Default 90, which is enough for several sheet openings.
#>
param(
    [int]$Seconds = 90,
    [switch]$All
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$exe = Join-Path $root 'src\Receiver.Diagnostics\bin\Release\net10.0-windows\airdrop-diag.exe'

if (-not (Test-Path $exe)) {
    Write-Host "Diagnostics tool not built. Building now..." -ForegroundColor Yellow
    dotnet build (Join-Path $root 'src\Receiver.Diagnostics') -c Release -v quiet
}

Write-Host ""
Write-Host "=== Test A1: iPhone AirDrop browse over LAN ===" -ForegroundColor Cyan
Write-Host ""
Write-Host "This PC listens on: " -NoNewline
(Get-NetIPAddress -AddressFamily IPv4 -InterfaceAlias 'Ethernet' -ErrorAction SilentlyContinue).IPAddress |
    ForEach-Object { Write-Host $_ -ForegroundColor Green }
Write-Host ""
Write-Host "Do this on the iPhone while the countdown runs:" -ForegroundColor Yellow
Write-Host "  1. Make sure the iPhone is on the SAME Wi-Fi as this PC's network (192.168.178.x)."
Write-Host "  2. Wi-Fi ON and Bluetooth ON (AirDrop needs both; BLE starts the discovery)."
Write-Host "  3. Open Photos or Files, pick a file, tap Share, then tap AirDrop."
Write-Host "  4. Leave the AirDrop screen open for ~15 seconds."
Write-Host "  5. Close it, wait a moment, and repeat once or twice."
Write-Host ""
Write-Host "Listening for $Seconds seconds..." -ForegroundColor Cyan
Write-Host ""

$extra = @()
if ($All) { $extra += '--all' }

& $exe mdns-watch --seconds $Seconds @extra
$code = $LASTEXITCODE

Write-Host ""
if ($code -eq 0) {
    Write-Host "RESULT: AirDrop browse traffic WAS seen on this LAN. Path A stays open." -ForegroundColor Green
} else {
    Write-Host "RESULT: No foreign _airdrop._tcp query seen in this window." -ForegroundColor Yellow
    Write-Host "        Re-run once before treating this as final." -ForegroundColor Yellow
}
exit $code
