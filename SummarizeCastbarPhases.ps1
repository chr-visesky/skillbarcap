Param(
    [string]$Path = "phase_castbar.txt"
)

if (-not (Test-Path $Path)) {
    Write-Error "File not found: $Path"
    exit 1
}

$lines = Get-Content $Path | Where-Object { $_ -match '^[0-9]{4} ' }

# Build per-frame table: index, macro stage, internal stage
$frames = @()
foreach ($l in $lines) {
    if ($l -match '^([0-9]{4}) ') {
        $idx = [int]$matches[1]
    } else {
        continue
    }

    $stage = "Idle"
    if ($l -match 'stage=([A-Za-z]+),') {
        $stage = $matches[1]
    }

    $istage = "Idle"
    if ($l -match 'istage=([A-Za-z]+)') {
        $istage = $matches[1]
    }

    $frames += [PSCustomObject]@{
        Index  = $idx
        Stage  = $stage
        IStage = $istage
    }
}

if ($frames.Count -eq 0) {
    Write-Error "No frame lines found."
    exit 1
}

# Detect cycles based on internal-stage transitions Idle -> non-Idle
$cycles = @()
$current = $null
$prevIst = "Idle"

foreach ($f in $frames) {
    $idx = $f.Index
    $ist = $f.IStage
    $stage = $f.Stage

    if ($prevIst -eq "Idle" -and $ist -ne "Idle") {
        if ($current -ne $null) { $cycles += $current }
        $current = [PSCustomObject]@{
            StartIndex = $idx
            EndIndex   = $idx
        }
    }

    if ($current -ne $null) {
        $current.EndIndex = $idx
    }

    $prevIst = $ist
}

if ($current -ne $null) { $cycles += $current }

for ($ci = 0; $ci -lt $cycles.Count; $ci++) {
    $c = $cycles[$ci]
    $start = $c.StartIndex
    $end   = $c.EndIndex

    $framesInCycle = $frames | Where-Object { $_.Index -ge $start -and $_.Index -le $end } | Sort-Object Index

    # Segment by macro stage
    $segments = @()
    $segStage = $null
    $segStart = $null

    foreach ($f in $framesInCycle) {
        if ($segStage -eq $null) {
            $segStage = $f.Stage
            $segStart = $f.Index
            continue
        }

        if ($f.Stage -ne $segStage) {
            $segments += [PSCustomObject]@{
                Stage = $segStage
                From  = $segStart
                To    = $f.Index - 1
            }
            $segStage = $f.Stage
            $segStart = $f.Index
        }
    }

    if ($segStage -ne $null) {
        $segments += [PSCustomObject]@{
            Stage = $segStage
            From  = $segStart
            To    = $end
        }
    }

    Write-Output ("Cycle#{0} [{1:D4}-{2:D4}]" -f $ci, $start, $end)
    foreach ($s in $segments) {
        Write-Output ("  {0,-8}: {1:D4}-{2:D4}" -f $s.Stage, $s.From, $s.To)
    }
}

