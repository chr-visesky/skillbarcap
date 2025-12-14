Param(
    [string]$Path = "phase_castbar.txt"
)

if (-not (Test-Path $Path)) {
    Write-Error "File not found: $Path"
    exit 1
}

$lines = Get-Content $Path | Where-Object { $_ -match '^[0-9]{4} ' }
$cycles = @()
$current = $null
$prevIst = "Idle"

foreach ($l in $lines) {
    if ($l -match '^([0-9]{4}) ') {
        $idx = [int]$matches[1]
    } else {
        continue
    }

    if ($l -match 'istage=([A-Za-z]+)') {
        $ist = $matches[1]
    } else {
        continue
    }

    if ($l -match 'stage=([A-Za-z]+),') {
        $macro = $matches[1]
    } else {
        $macro = "Idle"
    }

    if ($prevIst -eq "Idle" -and $ist -ne "Idle") {
        if ($current -ne $null) { $cycles += $current }
        $current = [PSCustomObject]@{
            StartIndex = $idx
            EndIndex   = $idx
            Fill       = $false
            TurnLight  = $false
            Turnout    = $false
        }
    }

    if ($current -ne $null) {
        $current.EndIndex = $idx
        switch ($macro) {
            "Fill"      { $current.Fill = $true }
            "TurnLight" { $current.TurnLight = $true }
            "Turnout"   { $current.Turnout = $true }
        }
    }

    $prevIst = $ist
}

if ($current -ne $null) { $cycles += $current }

$total = $cycles.Count
$fullCycles = $cycles | Where-Object { $_.Fill -and $_.TurnLight -and $_.Turnout }
$full  = if ($fullCycles) { $fullCycles.Count } else { 0 }

for ($i = 0; $i -lt $cycles.Count; $i++) {
    $c = $cycles[$i]
    Write-Output ("Cycle#{0}: [{1:D4}-{2:D4}] Fill={3}, TurnLight={4}, Turnout={5}" -f `
        $i, $c.StartIndex, $c.EndIndex, $c.Fill, $c.TurnLight, $c.Turnout)
}

Write-Output "TotalCycles=$total"
Write-Output "FullCycles=$full"
