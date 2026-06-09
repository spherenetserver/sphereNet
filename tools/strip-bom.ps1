# UTF-8 BOM'lu .cs dosyalarini BOM'suz yeniden yazar (repo standardi).
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)
$count = 0
Get-ChildItem "D:\Projeler\Yunus\sphereNet\src" -Recurse -Filter *.cs |
    Where-Object { $_.FullName -notmatch '\\(obj|bin)\\' } |
    ForEach-Object {
        $bytes = [System.IO.File]::ReadAllBytes($_.FullName)
        if ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF) {
            $text = [System.IO.File]::ReadAllText($_.FullName, [System.Text.Encoding]::UTF8)
            [System.IO.File]::WriteAllText($_.FullName, $text, $utf8NoBom)
            $count++
            $_.FullName.Replace('D:\Projeler\Yunus\sphereNet\src\', '')
        }
    }
"BOM temizlenen: $count"
