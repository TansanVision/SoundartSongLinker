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

    $source = Join-Path $testRoot 'songs'
    $target = Join-Path $testRoot 'links'
    [void][System.IO.Directory]::CreateDirectory($source)
    [void][System.IO.Directory]::CreateDirectory($target)
    $song = Join-Path $source 'song-000'
    [void][System.IO.Directory]::CreateDirectory($song)
    [System.IO.File]::WriteAllText((Join-Path $song 'audio.ogg'), 'audio')
    [System.IO.File]::WriteAllText((Join-Path $target 'song-000.ogg'), 'existing file')

    $config.BeatSaberPath = $source
    $config.SoundartPath = $target
    $sync = [SoundartSongLinker.Services.SongSyncService]::new($config, (Join-Path $testRoot 'config.json'))
    $sync.Sync()
    if ($config.Links.Length -ne 0 -or [System.IO.File]::ReadAllText((Join-Path $target 'song-000.ogg')) -ne 'existing file') {
        throw '既存ファイルを保護できませんでした。'
    }

    $stale = Join-Path $testRoot ('.SoundartSongLinker-staging-' + [guid]::NewGuid().ToString('N'))
    [void][System.IO.Directory]::CreateDirectory($stale)
    $staleManifest = @{ Version = 1; TargetRoot = $target; Links = @{} } | ConvertTo-Json -Depth 4 -Compress
    [System.IO.File]::WriteAllText((Join-Path $stale 'staging.json'), $staleManifest)
    [System.IO.File]::WriteAllText((Join-Path $stale '.lock'), '')
    $sync.RecoverStaleStaging()
    if ([System.IO.Directory]::Exists($stale)) {
        throw '前回の一時フォルダを回収できませんでした。'
    }

    $active = Join-Path $testRoot ('.SoundartSongLinker-staging-' + [guid]::NewGuid().ToString('N'))
    [void][System.IO.Directory]::CreateDirectory($active)
    [System.IO.File]::WriteAllText((Join-Path $active 'staging.json'), $staleManifest)
    $activeLockPath = Join-Path $active '.lock'
    [System.IO.File]::WriteAllText($activeLockPath, '')
    $activeLock = [System.IO.File]::Open($activeLockPath, 'Open', 'ReadWrite', 'None')
    try {
        $sync.RecoverStaleStaging()
        if (-not [System.IO.Directory]::Exists($active)) {
            throw '使用中の一時フォルダを削除しました。'
        }
    } finally {
        $activeLock.Dispose()
    }
    $sync.RecoverStaleStaging()
    if ([System.IO.Directory]::Exists($active)) {
        throw 'ロック解放後の一時フォルダを回収できませんでした。'
    }

    $foreign = Join-Path $testRoot ('.SoundartSongLinker-staging-' + [guid]::NewGuid().ToString('N'))
    [void][System.IO.Directory]::CreateDirectory($foreign)
    [System.IO.File]::WriteAllText((Join-Path $foreign 'keep.txt'), 'keep')
    $sync.RecoverStaleStaging()
    if (-not [System.IO.File]::Exists((Join-Path $foreign 'keep.txt'))) {
        throw '所有を確認できないファイルを削除しました。'
    }

    $invalid = Join-Path $testRoot ('.SoundartSongLinker-staging-' + [guid]::NewGuid().ToString('N'))
    [void][System.IO.Directory]::CreateDirectory($invalid)
    $outside = Join-Path $testRoot 'outside.ogg'
    [System.IO.File]::WriteAllText($outside, 'keep')
    $invalidManifest = @{ Version = 1; TargetRoot = $target; Links = @{ '..\outside.ogg' = $outside } } |
        ConvertTo-Json -Depth 4 -Compress
    [System.IO.File]::WriteAllText((Join-Path $invalid 'staging.json'), $invalidManifest)
    [System.IO.File]::WriteAllText((Join-Path $invalid '.lock'), '')
    $sync.RecoverStaleStaging()
    if (-not [System.IO.File]::Exists($outside) -or -not [System.IO.Directory]::Exists($invalid)) {
        throw '不正な一時フォルダの内容を削除しました。'
    }

    $newSong = Join-Path $source 'song-001'
    [void][System.IO.Directory]::CreateDirectory($newSong)
    $newAudio = Join-Path $newSong 'audio.ogg'
    [System.IO.File]::WriteAllText($newAudio, 'audio')
    $creationDenied = $false
    try {
        $sync.Sync()
    } catch [System.InvalidOperationException] {
        if (-not $_.Exception.Message.Contains('シンボリックリンクを作成する権限')) {
            throw
        }
        $creationDenied = $true
    }

    $stagingFolders = @(Get-ChildItem -LiteralPath $testRoot -Directory -Filter '.SoundartSongLinker-staging-*')
    if ($stagingFolders.Count -ne 2 -or
        -not ($stagingFolders.FullName -contains $foreign) -or
        -not ($stagingFolders.FullName -contains $invalid)) {
        throw '今回作成した一時フォルダが残りました。'
    }
    $published = Join-Path $target 'song-001.ogg'
    if ($creationDenied) {
        if ($config.Links.Length -ne 0 -or [System.IO.File]::Exists($published)) {
            throw '権限エラー後に未完成のリンクが公開されました。'
        }
    } else {
        $publishedLink = [System.IO.FileInfo]::new($published)
        if ($config.Links.Length -ne 1 -or $publishedLink.LinkTarget -ne $newAudio) {
            throw '検証したリンクが正しく公開されませんでした。'
        }
        $sync.UnlinkAll()
    }

    $records = [System.Collections.Generic.List[SoundartSongLinker.Models.LinkRecord]]::new()
    for ($i = 0; $i -lt 101; $i++) {
        $record = [SoundartSongLinker.Models.LinkRecord]::new()
        $record.LinkPath = Join-Path $target ('missing-{0:D3}.ogg' -f $i)
        $record.SourcePath = Join-Path $source ('missing-{0:D3}.ogg' -f $i)
        $records.Add($record)
    }
    $config.Links = $records.ToArray()
    $emptySource = Join-Path $testRoot 'empty-songs'
    [void][System.IO.Directory]::CreateDirectory($emptySource)
    $config.BeatSaberPath = $emptySource
    $sync.Sync()
    $saved = Get-Content (Join-Path $testRoot 'config.json') -Raw | ConvertFrom-Json
    if ($config.Links.Length -ne 0 -or $saved.Links.Count -ne 0) {
        throw '差分同期で古い 101 件の管理情報を整理できませんでした。'
    }

    $config.Links = $records.ToArray()
    $sync.UnlinkAll()
    if ($config.Links.Length -ne 0) {
        throw 'リンク解除後に 101 件の管理情報が残りました。'
    }
} finally {
    $resolved = [System.IO.Path]::GetFullPath($testRoot)
    if ($resolved.StartsWith($tempRoot, [System.StringComparison]::OrdinalIgnoreCase) -and
        [System.IO.Path]::GetFileName($resolved).StartsWith('SoundartSongLinker-test-')) {
        Remove-Item -LiteralPath $resolved -Recurse -Force -ErrorAction SilentlyContinue
    }
}

Write-Output 'Smoke tests passed.'
