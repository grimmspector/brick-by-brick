param(
    [ValidateRange(8, 512)]
    [int]$Length = 128,

    [ValidateRange(1, 16)]
    [int]$Height = 4,

    [ValidateRange(30, 300)]
    [int]$TimeoutSeconds = 120,

    [switch]$CompactStatic
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$modDirectory = Join-Path $repoRoot 'brickbybrick\bin\Debug\Mods'
$assetDirectory = Join-Path $repoRoot 'brickbybrick\assets'
$runId = Get-Date -Format 'yyyyMMdd-HHmmss'
$artifactDirectory = Join-Path $repoRoot "artifacts\server-profile-runs\$runId"
$logDirectory = Join-Path $artifactDirectory 'Logs'
$isolatedModsDirectory = Join-Path $artifactDirectory 'Mods'
$profileLog = Join-Path $logDirectory 'brickbybrick-profile.log'
$serverMainLog = Join-Path $logDirectory 'server-main.log'
$reportCopy = Join-Path $repoRoot "artifacts\server-profile-$runId.log"
$serverRoot = $env:VINTAGE_STORY
$dependencySource = Join-Path $env:APPDATA 'VintagestoryData\Mods\AttributeRenderingLibrary-v3.1.5.zip'

if ([string]::IsNullOrWhiteSpace($serverRoot)) {
    throw 'VINTAGE_STORY is not set. Set it to the directory containing VintagestoryServer.dll.'
}

$serverDll = Join-Path $serverRoot 'VintagestoryServer.dll'
if (-not (Test-Path -LiteralPath $serverDll)) {
    throw "Could not find VintagestoryServer.dll at $serverDll."
}

if (-not (Test-Path -LiteralPath $modDirectory)) {
    throw "Could not find built mod output at $modDirectory. Build the Debug mod first."
}

if (-not (Test-Path -LiteralPath $dependencySource)) {
    throw "Could not find Brick by Brick's Attribute Rendering Library dependency at $dependencySource."
}

New-Item -ItemType Directory -Force -Path $artifactDirectory, $logDirectory, $isolatedModsDirectory | Out-Null
$dependencyDestination = Join-Path $isolatedModsDirectory (Split-Path -Leaf $dependencySource)
Copy-Item -LiteralPath $dependencySource -Destination $dependencyDestination -Force
$initialLogLength = if (Test-Path -LiteralPath $profileLog) { (Get-Item -LiteralPath $profileLog).Length } else { 0 }

$startInfo = [System.Diagnostics.ProcessStartInfo]::new()
$startInfo.FileName = 'dotnet'
$startInfo.WorkingDirectory = $serverRoot
$startInfo.UseShellExecute = $false
$startInfo.CreateNoWindow = $true
$startInfo.RedirectStandardInput = $true
$startInfo.RedirectStandardOutput = $true
$startInfo.RedirectStandardError = $true
$startInfo.ArgumentList.Add($serverDll)
$startInfo.ArgumentList.Add('--tracelog')
$startInfo.ArgumentList.Add('--dataPath')
$startInfo.ArgumentList.Add($artifactDirectory)
$startInfo.ArgumentList.Add('--logPath')
$startInfo.ArgumentList.Add($logDirectory)
$startInfo.ArgumentList.Add('--addModPath')
$startInfo.ArgumentList.Add($modDirectory)
$startInfo.ArgumentList.Add('--addOrigin')
$startInfo.ArgumentList.Add($assetDirectory)

$server = [System.Diagnostics.Process]::new()
$server.StartInfo = $startInfo
$server.EnableRaisingEvents = $true
$null = $server.Start()
$server.BeginOutputReadLine()
$server.BeginErrorReadLine()

try {
    $startupDeadline = [DateTime]::UtcNow.AddSeconds(90)
    $runningMarker = 'Dedicated Server now running'
    $serverReady = $false
    while ([DateTime]::UtcNow -lt $startupDeadline) {
        if ($server.HasExited) {
            throw "The profiling server exited before accepting commands. Exit code: $($server.ExitCode)."
        }

        if (Test-Path -LiteralPath $serverMainLog) {
            $stream = [System.IO.File]::Open($serverMainLog, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::ReadWrite)
            try {
                $reader = [System.IO.StreamReader]::new($stream)
                $serverLog = $reader.ReadToEnd()
                $reader.Dispose()
                if ($serverLog.Contains($runningMarker)) {
                    $serverReady = $true
                    break
                }
            }
            finally {
                $stream.Dispose()
            }
        }

        Start-Sleep -Milliseconds 500
    }

    if (-not $serverReady) {
        throw 'Timed out waiting for the dedicated server to finish world generation and begin running.'
    }

    $profileCommand = if ($CompactStatic) { 'servercompact' } else { 'serverrun' }
    $server.StandardInput.WriteLine("/bbbprofile $profileCommand $Length $Height")
    $server.StandardInput.Flush()

    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    $completionMarker = 'AUTOMATED SERVER WALL PROFILE COMPLETE'
    $completed = $false
    while ([DateTime]::UtcNow -lt $deadline) {
        if ($server.HasExited) {
            throw "The profiling server exited before writing its completion marker. Exit code: $($server.ExitCode)."
        }

        if (Test-Path -LiteralPath $profileLog) {
            $stream = [System.IO.File]::Open($profileLog, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::ReadWrite)
            try {
                if ($stream.Length -gt $initialLogLength) {
                    $stream.Position = $initialLogLength
                    $reader = [System.IO.StreamReader]::new($stream)
                    $newLog = $reader.ReadToEnd()
                    $reader.Dispose()
                    if ($newLog.Contains($completionMarker)) {
                        $newLog | Set-Content -LiteralPath $reportCopy -Encoding utf8
                        Write-Output "Server profile completed. Report: $reportCopy"
                        $completed = $true
                        break
                    }
                }
            }
            finally {
                $stream.Dispose()
            }
        }

        Start-Sleep -Milliseconds 500
    }

    if (-not $completed) {
        throw "Timed out after $TimeoutSeconds seconds waiting for the server profile completion marker."
    }
}
finally {
    if (-not $server.HasExited) {
        $server.StandardInput.WriteLine('stop')
        $server.StandardInput.Flush()
        if (-not $server.WaitForExit(20000)) {
            $server.Kill()
            $server.WaitForExit()
        }
    }

    $server.Dispose()
}
