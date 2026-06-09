# Cift-kodlama (UTF-8 -> cp1252 yanlis okuma) onarimi.
# Eslemeler kod noktalariyla kurulur; yazim explicit BOM'suz UTF-8.
function S([int[]]$codes) { -join ($codes | ForEach-Object { [char]$_ }) }
$map = [ordered]@{}
$map[(S 0x00E2,0x20AC,0x201D)] = [string][char]0x2014  # em dash
$map[(S 0x00E2,0x2020,0x2019)] = [string][char]0x2192  # right arrow
$map[(S 0x00E2,0x20AC,0x00A6)] = [string][char]0x2026  # ellipsis
$map[(S 0x00E2,0x201D,0x20AC)] = [string][char]0x2500  # box drawing
$map[(S 0x00E2,0x2020,0x0090)] = [string][char]0x2190  # left arrow
$map[(S 0x00E2,0x2020,0x201D)] = [string][char]0x2194  # left-right arrow
$map[(S 0x00C3,0x00A7)]        = [string][char]0x00E7  # c-cedilla
$map[(S 0x00C3,0x00B6)]        = [string][char]0x00F6  # o-umlaut
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)
$fixedCount = 0
Get-ChildItem "D:\Projeler\Yunus\sphereNet\src" -Recurse -Filter *.cs |
    Where-Object { $_.FullName -notmatch '\\(obj|bin)\\' } |
    ForEach-Object {
        $c = [System.IO.File]::ReadAllText($_.FullName, [System.Text.Encoding]::UTF8)
        $orig = $c
        foreach ($k in $map.Keys) { $c = $c.Replace($k, $map[$k]) }
        if ($c -ne $orig) {
            [System.IO.File]::WriteAllText($_.FullName, $c, $utf8NoBom)
            $fixedCount++
            $_.FullName.Replace('D:\Projeler\Yunus\sphereNet\src\', '')
        }
    }
"onarilan dosya: $fixedCount"
