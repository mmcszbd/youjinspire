$ErrorActionPreference = 'Stop'

$candidates = @(
    'D:\Slay the Spire 2\data_sts2_windows_x86_64\sts2.dll',
    'D:\Slay the Spire 2\sts2.dll'
)
$p = $candidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (-not $p) {
    Write-Output 'NO sts2.dll found in known paths'
    exit 1
}

$len = (Get-Item -LiteralPath $p).Length
Write-Output "FILE  $p  ($len B)"

$bytes = [IO.File]::ReadAllBytes($p)
$pattern = 'MaxPlayers|PlayerLimit|MaxLobby|MaxCoop|MaxParticipants|LobbySize|MaxClients|MaxPlayerCount|maxPlayers'

foreach ($enc in @('Unicode', 'UTF8')) {
    Write-Output "--- $enc ---"
    $text = if ($enc -eq 'Unicode') {
        [Text.Encoding]::Unicode.GetString($bytes)
    } else {
        [Text.Encoding]::UTF8.GetString($bytes)
    }
    $hits = [regex]::Matches($text, '[\x20-\x7E]{4,70}') |
        ForEach-Object { $_.Value } |
        Where-Object { $_ -match $pattern } |
        Select-Object -Unique
    if (-not $hits) {
        Write-Output '(no match)'
    } else {
        $hits | Select-Object -First 40 | ForEach-Object { Write-Output $_ }
    }
}
