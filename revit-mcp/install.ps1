# One-step installer for the Revit 2025 MCP bridge.
# Installs .NET 8 SDK and Node.js if missing, builds and installs the Revit add-in,
# installs the MCP server, and registers it with Claude Desktop and Claude Code.
# Run with install.cmd (double-click), or:
#   powershell -ExecutionPolicy Bypass -File install.ps1

$ErrorActionPreference = 'Stop'
$root      = Split-Path -Parent $MyInvocation.MyCommand.Path
$addinDir  = Join-Path $root 'RevitMcpAddin'
$serverDir = Join-Path $root 'server'
$serverJs  = Join-Path $serverDir 'index.js'

function Step($msg) { Write-Host "`n==> $msg" -ForegroundColor Cyan }
function Ok($msg)   { Write-Host "    $msg" -ForegroundColor Green }
function Warn($msg) { Write-Host "    $msg" -ForegroundColor Yellow }

function Refresh-Path {
    $env:Path = [Environment]::GetEnvironmentVariable('Path', 'Machine') + ';' +
                [Environment]::GetEnvironmentVariable('Path', 'User')
}

function Ensure-Tool($name, $isInstalled, $wingetId) {
    if (& $isInstalled) { Ok "$name is installed"; return }
    if (-not (Get-Command winget -ErrorAction SilentlyContinue)) {
        throw "$name is missing and winget isn't available. Install $name manually, then run this again."
    }
    Warn "$name not found. Installing with winget (accept any Windows prompt)..."
    winget install --id $wingetId -e --accept-source-agreements --accept-package-agreements --silent
    Refresh-Path
    if (-not (& $isInstalled)) { throw "$name still not found after install. Restart your PC and run this again." }
    Ok "$name installed"
}

# Writes the MCP server entry into a Claude Desktop config file, keeping everything else.
function Set-ClaudeDesktopConfig($configPath, $nodeExe) {
    $dir = Split-Path -Parent $configPath
    if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }

    $cfg = [pscustomobject]@{}
    if (Test-Path $configPath) {
        Copy-Item $configPath "$configPath.bak" -Force
        $text = Get-Content $configPath -Raw
        if ($text -and $text.Trim()) { $cfg = $text | ConvertFrom-Json }
    }
    if (-not ($cfg.PSObject.Properties.Name -contains 'mcpServers')) {
        $cfg | Add-Member -NotePropertyName mcpServers -NotePropertyValue ([pscustomobject]@{})
    }
    $entry = [pscustomobject]@{ command = $nodeExe; args = @($serverJs) }
    $cfg.mcpServers | Add-Member -NotePropertyName revit -NotePropertyValue $entry -Force

    $json = $cfg | ConvertTo-Json -Depth 20
    [IO.File]::WriteAllText($configPath, $json, (New-Object Text.UTF8Encoding $false))
    Ok "Updated $configPath"
}

try {
    Write-Host "Revit 2025 MCP bridge installer" -ForegroundColor White

    Step "Checking Revit 2025"
    if (Test-Path 'C:\Program Files\Autodesk\Revit 2025\Revit.exe') { Ok 'Revit 2025 found' }
    else { Warn 'Revit 2025 not found in the default folder. Continuing anyway.' }

    if (Get-Process -Name Revit -ErrorAction SilentlyContinue) {
        Warn 'Revit is running. Please save your work and close Revit, then press Enter.'
        Read-Host | Out-Null
        if (Get-Process -Name Revit -ErrorAction SilentlyContinue) { throw 'Revit is still running. Close it and run this again.' }
    }

    Step 'Checking prerequisites'
    Ensure-Tool '.NET 8 SDK' { (Get-Command dotnet -ErrorAction SilentlyContinue) -and ((dotnet --list-sdks) -match '^8\.') } 'Microsoft.DotNet.SDK.8'
    Ensure-Tool 'Node.js'    { [bool](Get-Command node -ErrorAction SilentlyContinue) } 'OpenJS.NodeJS.LTS'

    Step 'Building and installing the Revit add-in'
    dotnet build $addinDir -c Release --nologo -v quiet
    if ($LASTEXITCODE -ne 0) { throw 'The add-in build failed (see the errors above).' }
    $addinsRoot = Join-Path $env:APPDATA 'Autodesk\Revit\Addins\2025'
    if (-not (Test-Path (Join-Path $addinsRoot 'RevitMcp\RevitMcpAddin.dll'))) { throw "The add-in wasn't copied to $addinsRoot." }
    Ok "Installed to $addinsRoot"

    Step 'Installing the MCP server'
    Push-Location $serverDir
    try { npm install --omit=dev --no-audit --no-fund --loglevel=error; if ($LASTEXITCODE -ne 0) { throw 'npm install failed.' } }
    finally { Pop-Location }
    Ok 'MCP server ready'

    Step 'Connecting Claude'
    $nodeExe = (Get-Command node).Source
    $connected = $false

    # Claude Desktop: regular installer, plus the Microsoft Store version if present
    $desktopConfigs = @(Join-Path $env:APPDATA 'Claude\claude_desktop_config.json')
    Get-ChildItem (Join-Path $env:LOCALAPPDATA 'Packages') -Directory -Filter 'Claude_*' -ErrorAction SilentlyContinue |
        ForEach-Object { $desktopConfigs += Join-Path $_.FullName 'LocalCache\Roaming\Claude\claude_desktop_config.json' }
    $hasDesktop = (Test-Path (Join-Path $env:APPDATA 'Claude')) -or ($desktopConfigs.Count -gt 1)
    if ($hasDesktop) {
        foreach ($c in $desktopConfigs) {
            if ((Test-Path (Split-Path -Parent $c)) -or $c -eq $desktopConfigs[0]) { Set-ClaudeDesktopConfig $c $nodeExe }
        }
        $connected = $true
    }

    # Claude Code CLI
    if (Get-Command claude -ErrorAction SilentlyContinue) {
        try { claude mcp remove revit --scope user *> $null } catch { }  # ignore "not found"
        claude mcp add revit --scope user -- $nodeExe $serverJs
        if ($LASTEXITCODE -eq 0) { Ok 'Registered with Claude Code'; $connected = $true }
    }

    if (-not $connected) {
        Warn 'Claude Desktop / Claude Code not found. Install Claude Desktop from https://claude.ai/download and run this again.'
    }

    Write-Host "`nAll done." -ForegroundColor Green
    Write-Host @"

Next:
  1. Fully quit Claude Desktop (right-click its tray icon > Quit) and open it again.
  2. Start Revit 2025 and click "Always Load" when it asks about RevitMcp.
  3. Open a project, then ask Claude: "Ping Revit and list the levels."
"@
}
catch {
    Write-Host "`nInstall failed: $($_.Exception.Message)" -ForegroundColor Red
    exit 1
}
