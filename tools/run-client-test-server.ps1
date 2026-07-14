param(
    [ValidateSet('Start', 'Status', 'Stop', 'Command', 'Serve')]
    [string]$Action = 'Start',

    [ValidateRange(1024, 65535)]
    [int]$Port = 42421,

    [ValidateRange(30, 180)]
    [int]$StartupTimeoutSeconds = 90,

    [string]$ModArchive,

    [string]$SessionPath,

    [string]$Command,

    [string]$TestOperatorName = 'GrimmSpector'
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$sessionRoot = Join-Path $repoRoot 'artifacts\client-test-server'
$activeSessionPath = Join-Path $sessionRoot 'active-session.json'
$modDirectory = Join-Path $repoRoot 'brickbybrick\bin\Debug\Mods'
$assetDirectory = Join-Path $repoRoot 'brickbybrick\assets'
$serverRoot = $env:VINTAGE_STORY
$dependencySource = Join-Path $env:APPDATA 'VintagestoryData\Mods\AttributeRenderingLibrary-v3.1.5.zip'

function Write-SessionState {
    param(
        [string]$Path,
        [hashtable]$State
    )

    $directory = Split-Path -Parent $Path
    New-Item -ItemType Directory -Force -Path $directory | Out-Null
    $State | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $Path -Encoding utf8
}

function Read-SessionState {
    param([string]$Path)

    if (-not (Test-Path -LiteralPath $Path)) {
        return $null
    }

    return Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
}

function Get-ClientAddresses {
    param([int]$ServerPort)

    $addresses = @('127.0.0.1:' + $ServerPort)
    $lanAddresses = Get-NetIPAddress -AddressFamily IPv4 -ErrorAction SilentlyContinue |
        Where-Object {
            $_.IPAddress -ne '127.0.0.1' -and
            $_.IPAddress -notlike '169.254.*' -and
            $_.PrefixOrigin -ne 'WellKnown'
        } |
        Select-Object -ExpandProperty IPAddress -Unique

    foreach ($address in $lanAddresses) {
        $addresses += "$address`:$ServerPort"
    }

    return $addresses
}

function Assert-ServerPrerequisites {
    param([string]$ArchivePath)

    if ([string]::IsNullOrWhiteSpace($serverRoot)) {
        throw 'VINTAGE_STORY is not set. Set it to the directory containing VintagestoryServer.dll.'
    }

    $serverDll = Join-Path $serverRoot 'VintagestoryServer.dll'
    if (-not (Test-Path -LiteralPath $serverDll)) {
        throw "Could not find VintagestoryServer.dll at $serverDll."
    }

    if ([string]::IsNullOrWhiteSpace($ArchivePath) -and -not (Test-Path -LiteralPath $modDirectory)) {
        throw "Could not find built mod output at $modDirectory. Build the Debug mod first."
    }

    if (-not [string]::IsNullOrWhiteSpace($ArchivePath) -and -not (Test-Path -LiteralPath $ArchivePath -PathType Leaf)) {
        throw "Could not find test mod archive at $ArchivePath."
    }

    if (-not (Test-Path -LiteralPath $dependencySource)) {
        throw "Could not find Brick by Brick's Attribute Rendering Library dependency at $dependencySource."
    }
}

function Start-ClientTestServer {
    Assert-ServerPrerequisites $ModArchive
    New-Item -ItemType Directory -Force -Path $sessionRoot | Out-Null

    $existing = Read-SessionState $activeSessionPath
    if ($null -ne $existing -and $existing.ServerProcessId -and (Get-Process -Id $existing.ServerProcessId -ErrorAction SilentlyContinue)) {
        throw "A client-test server is already active at $($existing.SessionPath). Run this script with -Action Status or -Action Stop first."
    }

    $runId = Get-Date -Format 'yyyyMMdd-HHmmss'
    $newSessionPath = Join-Path $sessionRoot $runId
    New-Item -ItemType Directory -Force -Path $newSessionPath | Out-Null

    $arguments = @(
        '-NoProfile',
        '-ExecutionPolicy', 'Bypass',
        '-File', "`"$PSCommandPath`"",
        '-Action', 'Serve',
        '-SessionPath', "`"$newSessionPath`"",
        '-Port', $Port,
        '-TestOperatorName', "`"$TestOperatorName`""
    )

    if (-not [string]::IsNullOrWhiteSpace($ModArchive)) {
        $arguments += @('-ModArchive', "`"$([IO.Path]::GetFullPath($ModArchive))`"")
    }

    $controller = Start-Process -FilePath (Join-Path $PSHOME 'pwsh.exe') -ArgumentList $arguments -WindowStyle Hidden -PassThru
    $deadline = [DateTime]::UtcNow.AddSeconds($StartupTimeoutSeconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        $state = Read-SessionState $activeSessionPath
        $isCurrentSession = $null -ne $state -and $state.SessionPath -eq $newSessionPath
        if ($isCurrentSession -and $state.Status -eq 'Ready') {
            Write-Output "Client-test server ready. Connect with: $($state.ClientAddresses -join ', ')"
            Write-Output "Session: $($state.SessionPath)"
            return
        }

        if ($isCurrentSession -and $state.Status -eq 'Failed') {
            throw "Client-test server failed to start. See $($state.ServerLogPath)"
        }

        if ($controller.HasExited) {
            throw "Client-test server controller exited before startup completed. Exit code: $($controller.ExitCode)."
        }

        Start-Sleep -Milliseconds 500
    }

    throw "Timed out after $StartupTimeoutSeconds seconds waiting for the client-test server to start."
}

function Show-ClientTestServerStatus {
    $state = Read-SessionState $activeSessionPath
    if ($null -eq $state) {
        Write-Output 'No client-test server session is recorded.'
        return
    }

    $running = $state.ServerProcessId -and (Get-Process -Id $state.ServerProcessId -ErrorAction SilentlyContinue)
    Write-Output "Status: $($state.Status); server process running: $([bool]$running)"
    Write-Output "Test profile: $($state.TestProfile)"
    Write-Output "Connect with: $($state.ClientAddresses -join ', ')"
    Write-Output "Session: $($state.SessionPath)"
    Write-Output "Server log: $($state.ServerLogPath)"
}

function Stop-ClientTestServer {
    $state = Read-SessionState $activeSessionPath
    if ($null -eq $state) {
        Write-Output 'No client-test server session is recorded.'
        return
    }

    $stopRequest = Join-Path $state.SessionPath 'stop.request'
    New-Item -ItemType File -Force -Path $stopRequest | Out-Null
    $deadline = [DateTime]::UtcNow.AddSeconds(30)
    while ([DateTime]::UtcNow -lt $deadline) {
        $updated = Read-SessionState $activeSessionPath
        if ($null -eq $updated -or $updated.Status -eq 'Stopped') {
            Write-Output 'Client-test server stopped.'
            return
        }

        Start-Sleep -Milliseconds 500
    }

    throw "The client-test server did not stop within 30 seconds. See $($state.ServerLogPath)."
}

function Initialize-CreativeSuperflatConfiguration {
    param(
        [string]$TestSessionPath,
        [int]$ServerPort
    )

    $configurationPath = Join-Path $TestSessionPath 'serverconfig.json'
    $savePath = Join-Path $TestSessionPath 'Saves\default.vcdbs'
    $creativePlayerRole = [ordered]@{
        Code = 'crplayer'
        PrivilegeLevel = 100
        Name = 'Creative Player'
        Description = 'Creative access for local test clients.'
        DefaultSpawn = $null
        ForcedSpawn = $null
        Privileges = @(
            'controlplayergroups', 'manageplayergroups', 'chat', 'areamodify', 'build', 'useblock',
            'gamemode', 'freemove', 'attackcreatures', 'attackplayers', 'selfkill'
        )
        RuntimePrivileges = @()
        DefaultGameMode = 2
        Color = 'LightGreen'
        LandClaimAllowance = 1310720
        LandClaimMinSize = [ordered]@{ X = 5; Y = 5; Z = 5 }
        LandClaimMaxAreas = 6
        AutoGrant = $false
    }
    $testAdminRole = [ordered]@{
        Code = 'admin'
        PrivilegeLevel = 99999
        Name = 'Creative Test Admin'
        Description = 'Full test-server permissions with creative mode enabled by default.'
        DefaultSpawn = $null
        ForcedSpawn = $null
        Privileges = @(
            'build', 'useblock', 'buildblockseverywhere', 'useblockseverywhere',
            'attackplayers', 'attackcreatures', 'freemove', 'gamemode', 'pickingrange',
            'chat', 'kick', 'ban', 'whitelist', 'setwelcome', 'announce', 'readlists',
            'give', 'areamodify', 'setspawn', 'controlserver', 'tp', 'time', 'grantrevoke',
            'root', 'commandplayer', 'controlplayergroups', 'manageplayergroups', 'selfkill',
            'manageotherplayergroups', 'worldedit'
        )
        RuntimePrivileges = @()
        DefaultGameMode = 2
        Color = 'LightBlue'
        LandClaimAllowance = 2147483647
        LandClaimMinSize = [ordered]@{ X = 5; Y = 5; Z = 5 }
        LandClaimMaxAreas = 99999
        AutoGrant = $true
    }
    $configuration = [ordered]@{
        ConfigVersion = '1.10'
        ServerName = 'Brick by Brick Test Server'
        Port = $ServerPort
        Roles = @($creativePlayerRole, $testAdminRole)
        DefaultRoleCode = 'crplayer'
        WorldConfig = [ordered]@{
            Seed = 'brickbybrick-creative-test-range'
            SaveFileLocation = $savePath
            WorldName = 'Brick by Brick Creative Test Range'
            AllowCreativeMode = $true
            PlayStyle = 'creativebuilding'
            PlayStyleLangCode = 'creativebuilding'
            WorldType = 'superflat'
            WorldConfiguration = [ordered]@{
                gameMode = 'creative'
            }
            MapSizeY = 256
            CreatedByPlayerName = 'Brick by Brick Test Runner'
            DisabledMods = $null
            RepairMode = $false
        }
    }

    $configuration | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $configurationPath -Encoding utf8
    return $configurationPath
}

function Send-ClientTestServerCommand {
    if ([string]::IsNullOrWhiteSpace($Command)) {
        throw 'Command requires -Command with one dedicated-server console command.'
    }

    $state = Read-SessionState $activeSessionPath
    if ($null -eq $state -or $state.Status -ne 'Ready') {
        throw 'No ready client-test server session is available.'
    }

    $commandRequest = Join-Path $state.SessionPath 'command.request'
    $Command | Set-Content -LiteralPath $commandRequest -Encoding ascii
    Write-Output "Queued server command: $Command"
}

function Serve-ClientTestServer {
    if ([string]::IsNullOrWhiteSpace($SessionPath)) {
        throw 'Serve requires -SessionPath.'
    }

    Assert-ServerPrerequisites $ModArchive
    $logDirectory = Join-Path $SessionPath 'Logs'
    $isolatedModsDirectory = Join-Path $SessionPath 'Mods'
    $serverMainLog = Join-Path $logDirectory 'server-main.log'
    $stopRequest = Join-Path $SessionPath 'stop.request'
    $commandRequest = Join-Path $SessionPath 'command.request'
    $serverDll = Join-Path $serverRoot 'VintagestoryServer.dll'
    New-Item -ItemType Directory -Force -Path $SessionPath, $logDirectory, $isolatedModsDirectory | Out-Null
    $serverConfigPath = Initialize-CreativeSuperflatConfiguration $SessionPath $Port
    Copy-Item -LiteralPath $dependencySource -Destination (Join-Path $isolatedModsDirectory (Split-Path -Leaf $dependencySource)) -Force
    if (-not [string]::IsNullOrWhiteSpace($ModArchive)) {
        Copy-Item -LiteralPath $ModArchive -Destination (Join-Path $isolatedModsDirectory (Split-Path -Leaf $ModArchive)) -Force
    }

    $state = @{
        Status = 'Starting'
        SessionPath = $SessionPath
        ServerLogPath = $serverMainLog
        ServerConfigPath = $serverConfigPath
        ServerProcessId = $null
        ControllerProcessId = $PID
        Port = $Port
        TestProfile = 'Creative Building / superflat'
        TestOperatorName = $TestOperatorName
        ClientAddresses = @()
        StartedUtc = [DateTime]::UtcNow.ToString('o')
    }
    Write-SessionState $activeSessionPath $state

    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = 'dotnet'
    $startInfo.WorkingDirectory = $serverRoot
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardInput = $true
    $startInfo.ArgumentList.Add($serverDll)
    $startInfo.ArgumentList.Add('--tracelog')
    $startInfo.ArgumentList.Add('--port')
    $startInfo.ArgumentList.Add([string]$Port)
    $startInfo.ArgumentList.Add('--dataPath')
    $startInfo.ArgumentList.Add($SessionPath)
    $startInfo.ArgumentList.Add('--logPath')
    $startInfo.ArgumentList.Add($logDirectory)
    $startInfo.ArgumentList.Add('--addModPath')
    $startInfo.ArgumentList.Add($(if ([string]::IsNullOrWhiteSpace($ModArchive)) { $modDirectory } else { $isolatedModsDirectory }))
    $startInfo.ArgumentList.Add('--addOrigin')
    $startInfo.ArgumentList.Add($assetDirectory)

    $server = [System.Diagnostics.Process]::new()
    $server.StartInfo = $startInfo
    try {
        $null = $server.Start()
        $state.ServerProcessId = $server.Id
        Write-SessionState $activeSessionPath $state

        $deadline = [DateTime]::UtcNow.AddSeconds($StartupTimeoutSeconds)
        while ([DateTime]::UtcNow -lt $deadline) {
            if ($server.HasExited) {
                throw "The client-test server exited during startup. Exit code: $($server.ExitCode)."
            }

            if ((Test-Path -LiteralPath $serverMainLog) -and (Select-String -LiteralPath $serverMainLog -SimpleMatch 'Dedicated Server now running' -Quiet)) {
                $state.Status = 'Ready'
                $state.ClientAddresses = @(Get-ClientAddresses $Port)
                Write-SessionState $activeSessionPath $state
                break
            }

            Start-Sleep -Milliseconds 500
        }

        if ($state.Status -ne 'Ready') {
            throw 'Timed out waiting for the dedicated server to finish world generation and begin running.'
        }

        $operatorPrepared = $false
        while (-not $server.HasExited) {
            if (Test-Path -LiteralPath $stopRequest) {
                $server.StandardInput.WriteLine('stop')
                $server.StandardInput.Flush()
                if (-not $server.WaitForExit(20000)) {
                    $server.Kill()
                    $server.WaitForExit()
                }
                break
            }

            if (Test-Path -LiteralPath $commandRequest) {
                $requestedCommand = (Get-Content -LiteralPath $commandRequest -Raw).Trim()
                Remove-Item -LiteralPath $commandRequest -Force
                if (-not [string]::IsNullOrWhiteSpace($requestedCommand)) {
                    $server.StandardInput.WriteLine($requestedCommand)
                    $server.StandardInput.Flush()
                }
            }

            if (-not $operatorPrepared -and (Test-Path -LiteralPath $serverMainLog)) {
                $joinedOperatorPattern = [regex]::Escape($TestOperatorName) + ' .* joins\\.'
                if (Select-String -LiteralPath $serverMainLog -Pattern $joinedOperatorPattern -Quiet) {
                    $server.StandardInput.WriteLine("/op $TestOperatorName")
                    $server.StandardInput.WriteLine("/gamemode $TestOperatorName 2")
                    $server.StandardInput.Flush()
                    $operatorPrepared = $true
                }
            }

            Start-Sleep -Milliseconds 500
        }

        $state.Status = 'Stopped'
        $state.StoppedUtc = [DateTime]::UtcNow.ToString('o')
        Write-SessionState $activeSessionPath $state
    }
    catch {
        $state.Status = 'Failed'
        $state.Error = $_.Exception.Message
        Write-SessionState $activeSessionPath $state
        throw
    }
    finally {
        $server.Dispose()
    }
}

switch ($Action) {
    'Start' { Start-ClientTestServer }
    'Status' { Show-ClientTestServerStatus }
    'Stop' { Stop-ClientTestServer }
    'Command' { Send-ClientTestServerCommand }
    'Serve' { Serve-ClientTestServer }
}
