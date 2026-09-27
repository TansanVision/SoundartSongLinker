$ErrorActionPreference = 'Stop'

$assembly = Join-Path $PSScriptRoot '..\SoundartSongLinker\bin\Debug\net10.0\SoundartSongLinker.dll'
Add-Type -Path $assembly

$vdf = @'
"libraryfolders"
{
    "0"
    {
        "path" "C:\\Steam"
        "apps" { "620980" "1" }
    }
}
'@

$root = [SoundartSongLinker.Services.SteamVdfParser]::Parse($vdf)
$libraries = [SoundartSongLinker.Services.SteamVdfParser]::GetObject($root, 'libraryfolders')
$first = $libraries['0']
if ([SoundartSongLinker.Services.SteamVdfParser]::GetString($first, 'path') -ne 'C:\Steam') {
    throw 'VDF のライブラリパスを解析できませんでした。'
}

$invalidRejected = $false
try {
    [void][SoundartSongLinker.Services.SteamVdfParser]::Parse('"libraryfolders" { "0" {')
} catch [System.FormatException] {
    $invalidRejected = $true
}
if (-not $invalidRejected) {
    throw '無効な VDF が受理されました。'
}

$config = [SoundartSongLinker.Models.AppConfig]::new()
$sync = [SoundartSongLinker.Services.SongSyncService]::new($config, 'unused.json')
$emptyPathRejected = $false
try {
    $sync.Sync()
} catch [System.InvalidOperationException] {
    $emptyPathRejected = $true
}
if (-not $emptyPathRejected) {
    throw '空の設定パスで同期が開始されました。'
}

$tempRoot = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
$testRoot = Join-Path $tempRoot ('SoundartSongLinker-test-' + [guid]::NewGuid().ToString('N'))
try {
    $gamePath = Join-Path $testRoot 'steamapps\common\Beat Saber'
    [void][System.IO.Directory]::CreateDirectory($gamePath)
    $manifest = Join-Path $testRoot 'steamapps\appmanifest_620980.acf'
    [System.IO.File]::WriteAllText($manifest, '"AppState" { "appid" "620980" "name" "Beat Saber" "installdir" "Beat Saber" }')

    $type = [SoundartSongLinker.Services.SteamLibraryService]
    $flags = [System.Reflection.BindingFlags]::NonPublic -bor [System.Reflection.BindingFlags]::Static
    $method = $type.GetMethod('ParseManifest', $flags)
    $game = $method.Invoke($null, [object[]]@($manifest.ToString(), $testRoot.ToString()))
    if ($null -eq $game -or $game.InstallDirectory -ne $gamePath) {
        throw 'ゲームのインストール先が絶対パスで返されませんでした。'
    }
} finally {
    $resolved = [System.IO.Path]::GetFullPath($testRoot)
    if ($resolved.StartsWith($tempRoot, [System.StringComparison]::OrdinalIgnoreCase) -and
        [System.IO.Path]::GetFileName($resolved).StartsWith('SoundartSongLinker-test-')) {
        Remove-Item -LiteralPath $resolved -Recurse -Force -ErrorAction SilentlyContinue
    }
}

Write-Output 'Smoke tests passed.'
