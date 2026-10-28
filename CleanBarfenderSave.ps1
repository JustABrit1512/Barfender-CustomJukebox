<#
 CleanBarfenderSave.ps1
 Removes custom-jukebox leftovers from a Barfender save so it works without the mod.

 Usage:
   .\CleanBarfenderSave.ps1 -Path "C:\path\to\BarfenderSave_1.json"
   .\CleanBarfenderSave.ps1 -Path "C:\path\to\save\folder"        (cleans every .json with Barfender/Save in its name)
   -OriginalCount 9   playlists in the unmodified game (default 9)
   -DryRun            show what would change, write nothing
#>
param(
    [Parameter(Mandatory = $true)][string]$Path,
    [int]$OriginalCount = 9,
    [switch]$DryRun
)
$ErrorActionPreference = 'Stop'

if (Test-Path -LiteralPath $Path -PathType Container) {
    $files = @(Get-ChildItem -LiteralPath $Path -Filter '*.json' -File |
        Where-Object { $_.Name -like '*Barfender*' -or $_.Name -like '*Save*' })
} elseif (Test-Path -LiteralPath $Path -PathType Leaf) {
    $files = @(Get-Item -LiteralPath $Path)
} else {
    throw "Not found: $Path"
}
if ($files.Count -eq 0) { Write-Host 'No save files found.'; return }

if (Get-Process -Name 'Barfender' -ErrorAction SilentlyContinue) {
    throw 'Barfender is running. Close the game first, or it will overwrite the changes.'
}

$keepMask = [int]((1L -shl $OriginalCount) - 1)   # 9 playlists -> 511

foreach ($file in $files) {
    $bytes  = [IO.File]::ReadAllBytes($file.FullName)
    $hasBom = $bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF
    $text   = [Text.Encoding]::UTF8.GetString($bytes)
    if ($hasBom) { $text = $text.Substring(1) }

    if ($text -notmatch '"UnlockedPlaylistsMask"') {
        Write-Host "$($file.Name): no jukebox data, skipped"
        continue
    }

    $changes = New-Object System.Collections.Generic.List[string]

    # Unlock mask: keep only the bits that belong to the game's own playlists
    $text = [regex]::Replace($text, '("UnlockedPlaylistsMask"\s*:\s*)(-?\d+)', {
        param($m)
        $old = [int]$m.Groups[2].Value
        $new = $old -band $keepMask
        if ($new -ne $old) { $changes.Add("UnlockedPlaylistsMask $old -> $new") }
        return $m.Groups[1].Value + $new
    })

    # Selected playlist: anything past the game's own playlists becomes "nothing playing"
    $text = [regex]::Replace($text, '("CurrentPlaylistIndex"\s*:\s*)(-?\d+)', {
        param($m)
        $old = [int]$m.Groups[2].Value
        if ($old -ge $OriginalCount) {
            $changes.Add("CurrentPlaylistIndex $old -> -1")
            return $m.Groups[1].Value + '-1'
        }
        return $m.Value
    })

    # Last vinyl found: reset to the game's default if it points at a custom slot
    $text = [regex]::Replace($text, '("LastFoundVinylPlaylistIndex"\s*:\s*)(-?\d+)', {
        param($m)
        $old = [int]$m.Groups[2].Value
        if ($old -ge $OriginalCount) {
            $changes.Add("LastFoundVinylPlaylistIndex $old -> 0")
            return $m.Groups[1].Value + '0'
        }
        return $m.Value
    })

    if ($changes.Count -eq 0) { Write-Host "$($file.Name): already clean"; continue }

    Write-Host "$($file.Name):"
    foreach ($c in $changes) { Write-Host "  $c" }
    if ($DryRun) { Write-Host '  (dry run - nothing written)'; continue }

    $backup = "$($file.FullName).bak-$(Get-Date -Format 'yyyyMMdd-HHmmss')"
    Copy-Item -LiteralPath $file.FullName -Destination $backup
    [IO.File]::WriteAllText($file.FullName, $text, (New-Object System.Text.UTF8Encoding($hasBom)))
    Write-Host "  cleaned. Backup: $backup"
}