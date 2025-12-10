$warning = @"

> 🚨🚨 WARNING 🚨🚨 - Though in the 1.x range of version THINGS WILL STILL BREAK. This is the lab for developing this concept when stabilized it'll becoe the first *stylo*flow release 🚨🚨🚨

"@

Get-ChildItem -Recurse -Filter "README.md" | ForEach-Object {
    $content = Get-Content $_.FullName -Raw

    # Skip if already has warning
    if ($content -match "🚨🚨 WARNING 🚨🚨") {
        Write-Host "Skipping (already has warning): $($_.FullName)"
        return
    }

    $lines = Get-Content $_.FullName

    # Find end of first paragraph (first blank line after content)
    $firstParaEnd = -1
    for ($i = 1; $i -lt $lines.Count; $i++) {
        if ($lines[$i] -match "^\s*$" -and $lines[$i-1] -notmatch "^\s*$" -and $lines[$i-1] -notmatch "^#") {
            $firstParaEnd = $i
            break
        }
    }

    if ($firstParaEnd -gt 0) {
        $before = $lines[0..($firstParaEnd-1)]
        $after = $lines[$firstParaEnd..($lines.Count-1)]

        $newContent = $before + $warning.Split("`n") + $after
        Set-Content -Path $_.FullName -Value ($newContent -join "`n") -NoNewline

        Write-Host "Updated: $($_.FullName)"
    } else {
        Write-Host "Skipping (no paragraph break found): $($_.FullName)"
    }
}
