$ErrorActionPreference = 'Stop'

$src = 'D:\YouJinSpire\.godot\mono\temp\bin\Release\youjin.dll'
if (-not (Test-Path -LiteralPath $src)) {
    Write-Output "MISSING SOURCE $src"
    exit 1
}

$hash  = (Get-FileHash -LiteralPath $src -Algorithm SHA256).Hash.Substring(0, 12)
$size  = (Get-Item -LiteralPath $src).Length
$mtime = (Get-Item -LiteralPath $src).LastWriteTime.ToString('yyyy-MM-dd HH:mm:ss')
Write-Output "SRC  $size B  sha=$hash  time=$mtime"

$dst = @(
    'D:\Slay the Spire 2\mods\YouJinSpire',
    'D:\SteamLibrary\steamapps\common\Slay the Spire 2\mods\YouJinSpire',
    'D:\SteamLibrary\steamapps\workshop\content\2868840\3807296531'
)

foreach ($d in $dst) {
    if (-not (Test-Path -LiteralPath $d)) {
        Write-Output "SKIP missing dir: $d"
        continue
    }
    $f = Join-Path $d 'youjin.dll'
    Copy-Item -LiteralPath $src -Destination $f -Force
    $h = (Get-FileHash -LiteralPath $f -Algorithm SHA256).Hash.Substring(0, 12)
    $s = (Get-Item -LiteralPath $f).Length
    Write-Output "OK   $s B  sha=$h  -> $d"
}
