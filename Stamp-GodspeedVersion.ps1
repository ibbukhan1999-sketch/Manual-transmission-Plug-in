param(
    [Parameter(Mandatory = $true)][string]$ProjectDirectory,
    [Parameter(Mandatory = $true)][string]$ProjectName
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$versionDirectory = Join-Path $ProjectDirectory 'obj'
$stateDirectory = if ([string]::IsNullOrWhiteSpace($env:LOCALAPPDATA)) {
    $versionDirectory
} else {
    Join-Path $env:LOCALAPPDATA 'ETS2LA\GodspeedBuildVersions'
}
$statePath = Join-Path $stateDirectory ($ProjectName + '.state')
$previousLocalState = Join-Path $versionDirectory 'godspeed-version.state'
$dayPath = Join-Path $versionDirectory 'godspeed-build-day.txt'
$sequencePath = Join-Path $versionDirectory 'godspeed-build-sequence.txt'
$utcPath = Join-Path $versionDirectory 'godspeed-build-utc.txt'
$mutexName = 'Local\GodspeedVersion_' + ($ProjectName -replace '[^A-Za-z0-9_]', '_')
$mutex = [Threading.Mutex]::new($false, $mutexName)
$lockHeld = $false

try {
    try {
        $lockHeld = $mutex.WaitOne([TimeSpan]::FromMinutes(2))
    } catch [Threading.AbandonedMutexException] {
        $lockHeld = $true
    }
    if (-not $lockHeld) {
        throw "Timed out waiting for the $ProjectName version counter."
    }

    [IO.Directory]::CreateDirectory($versionDirectory) | Out-Null
    [IO.Directory]::CreateDirectory($stateDirectory) | Out-Null
    if (-not [IO.File]::Exists($statePath) -and [IO.File]::Exists($previousLocalState)) {
        [IO.File]::Copy($previousLocalState, $statePath)
    }
    $now = [DateTime]::UtcNow
    $baseline = [DateTime]::SpecifyKind([DateTime]::new(2020, 1, 1), [DateTimeKind]::Utc)
    $day = [int]($now.Date - $baseline).TotalDays
    $previousDay = -1
    $previousSequence = 0

    if ([IO.File]::Exists($statePath)) {
        $parts = [IO.File]::ReadAllText($statePath).Trim().Split(',')
        if ($parts.Length -ne 2 -or
            -not [int]::TryParse($parts[0], [ref]$previousDay) -or
            -not [int]::TryParse($parts[1], [ref]$previousSequence)) {
            throw "Invalid $ProjectName version state: $statePath"
        }
    }

    # A clock rollback cannot make the next Windows file version go backwards.
    $day = [Math]::Max($day, $previousDay)
    $sequence = if ($day -eq $previousDay) { $previousSequence + 1 } else { 1 }
    if ($day -gt 65534 -or $sequence -gt 65534) {
        throw "The $ProjectName Windows file-version component exceeded 65534."
    }

    $stamp = $now.ToString('yyyyMMddTHHmmssZ', [Globalization.CultureInfo]::InvariantCulture)
    [IO.File]::WriteAllText($dayPath, [string]$day)
    [IO.File]::WriteAllText($sequencePath, [string]$sequence)
    [IO.File]::WriteAllText($utcPath, $stamp)
    [IO.File]::WriteAllText($statePath, "$day,$sequence")
} finally {
    if ($lockHeld) { $mutex.ReleaseMutex() }
    $mutex.Dispose()
}
