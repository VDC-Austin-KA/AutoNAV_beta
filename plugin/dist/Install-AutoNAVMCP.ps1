<#
  Install-AutoNAVMCP.ps1
  --------------------------------------------------------------------
  Installs the AutoNAV MCP plugin into every Navisworks Manage version
  detected on this machine. For each install it:
    1. Picks the DLL built for that exact Navisworks year (2024-2027).
    2. Copies it to  <Navisworks>\Plugins\AutoNAVMCP\AutoNAVMCP.dll
       (folder name MUST equal the assembly name — Navisworks only
        loads plugins from a subfolder named after the DLL).
    3. UNBLOCKS the DLL (Windows marks copied/downloaded files, and
       Navisworks silently refuses to load a blocked assembly — this is
       the #1 reason the ribbon button never appears).

  Run in an ELEVATED PowerShell (writes under C:\Program Files):
      powershell -ExecutionPolicy Bypass -File .\Install-AutoNAVMCP.ps1

  After it finishes: restart Navisworks -> Add-Ins tab -> AutoNAV MCP.
#>

$ErrorActionPreference = 'Stop'
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Definition

# year -> DLL path shipped alongside this script
$dllForYear = @{
  '2024' = Join-Path $scriptDir '2024\AutoNAVMCP.dll'
  '2025' = Join-Path $scriptDir '2025\AutoNAVMCP.dll'
  '2026' = Join-Path $scriptDir '2026\AutoNAVMCP.dll'
  '2027' = Join-Path $scriptDir '2027\AutoNAVMCP.dll'
}

Write-Host "AutoNAV MCP installer" -ForegroundColor Cyan
Write-Host "---------------------"

$installedAny = $false
foreach ($year in ($dllForYear.Keys | Sort-Object)) {
  $navRoot = Join-Path ${env:ProgramFiles} "Autodesk\Navisworks Manage $year"
  if (-not (Test-Path $navRoot)) { continue }

  $srcDll = $dllForYear[$year]
  if (-not (Test-Path $srcDll)) {
    Write-Warning "Navisworks Manage $year found, but no matching DLL at $srcDll — skipping."
    continue
  }

  $pluginDir = Join-Path $navRoot "Plugins\AutoNAVMCP"
  New-Item -ItemType Directory -Force -Path $pluginDir | Out-Null
  $destDll = Join-Path $pluginDir "AutoNAVMCP.dll"

  Copy-Item -Path $srcDll -Destination $destDll -Force

  # Critical: clear the Zone.Identifier "block" flag.
  Unblock-File -Path $destDll

  Write-Host ("  [OK] Navisworks Manage {0}  ->  {1}" -f $year, $destDll) -ForegroundColor Green
  $installedAny = $true
}

if (-not $installedAny) {
  Write-Warning "No Navisworks Manage 2024-2027 installations were found under '$env:ProgramFiles\Autodesk'."
  Write-Host   "If Navisworks is installed elsewhere, copy the matching year's AutoNAVMCP.dll into" -ForegroundColor Yellow
  Write-Host   "  <Navisworks Manage YEAR>\Plugins\AutoNAVMCP\AutoNAVMCP.dll  and run Unblock-File on it." -ForegroundColor Yellow
  exit 1
}

Write-Host ""
Write-Host "Done. Restart Navisworks, then open the Add-Ins ribbon tab and click 'AutoNAV MCP'." -ForegroundColor Cyan
Write-Host "The button toggles the bridge on 127.0.0.1:5711 (see README for the MCP server + client setup)."
