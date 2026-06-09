# Mojibake envanteri: U+00C2/U+00C3/U+00E2 ile baslayan dizileri listeler.
$marks = @([char]0x00C2, [char]0x00C3, [char]0x00E2)
$seqs = @{}
$files = New-Object System.Collections.Generic.List[string]
Get-ChildItem "D:\Projeler\Yunus\sphereNet\src" -Recurse -Filter *.cs |
    Where-Object { $_.FullName -notmatch '\\(obj|bin)\\' } |
    ForEach-Object {
        $c = [System.IO.File]::ReadAllText($_.FullName, [System.Text.Encoding]::UTF8)
        $found = $false
        for ($i = 0; $i -lt $c.Length - 2; $i++) {
            if ($marks -contains $c[$i]) {
                $len = [Math]::Min(3, $c.Length - $i)
                $seq = $c.Substring($i, $len)
                $key = (($seq.ToCharArray() | ForEach-Object { '{0:X4}' -f [int]$_ }) -join ' ')
                if ($seqs.ContainsKey($key)) { $seqs[$key] = $seqs[$key] + 1 } else { $seqs[$key] = 1 }
                $found = $true
            }
        }
        if ($found) { $files.Add($_.FullName.Replace('D:\Projeler\Yunus\sphereNet\src\', '')) }
    }
"etkilenen dosya: $($files.Count)"
$seqs.GetEnumerator() | Sort-Object Value -Descending | Select-Object -First 15 | ForEach-Object { "$($_.Key) x$($_.Value)" }
""
$files | Sort-Object | Select-Object -First 40
