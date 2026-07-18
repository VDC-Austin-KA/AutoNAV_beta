<#
  Uninstall-AutoNAVMCP.ps1
  Removes the AutoNAV MCP plugin folder from every Navisworks Manage
  version. Run in an ELEVATED PowerShell:
      powershell -ExecutionPolicy Bypass -File .\Uninstall-AutoNAVMCP.ps1
#>
$ErrorActionPreference = 'Stop'
$removed = $false
foreach ($year in '2024','2025','2026','2027') {
  $pluginDir = Join-Path ${env:ProgramFiles} "Autodesk\Navisworks Manage $year\Plugins\AutoNAVMCP"
  if (Test-Path $pluginDir) {
    Remove-Item -Recurse -Force $pluginDir
    Write-Host ("Removed {0}" -f $pluginDir) -ForegroundColor Green
    $removed = $true
  }
}
if (-not $removed) { Write-Host "Nothing to remove." -ForegroundColor Yellow }
