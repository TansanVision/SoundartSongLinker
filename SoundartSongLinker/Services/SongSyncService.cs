using SoundartSongLinker.Models;
using System.Text.Json;

namespace SoundartSongLinker.Services;

/// <summary>
/// アプリケーションの設定情報を管理し、リンク情報の同期を行うサービスクラスです。
/// </summary>
public sealed class SongSyncService
{
    private const string StagingPrefix = ".SoundartSongLinker-staging-";
    private const string StagingManifestName = "staging.json";
    private const string StagingLockName = ".lock";

    private sealed class StagingManifest
    {
        public int Version { get; set; } = 1;
        public string TargetRoot { get; set; } = string.Empty;
        public Dictionary<string, string> Links { get; set; } = [];
    }

    /// <summary>
    /// 設定情報を保持するオブジェクトです。
    /// </summary>
    private readonly AppConfig _config;

    /// <summary>
    /// 設定ファイルのパスを保持する文字列です。
    /// </summary>
    private readonly string _configPath;

    /// <summary>
    /// 文字列の比較を行うためのコンパレータです。
    /// </summary>
    private readonly StringComparer _comparer =
        StringComparer.OrdinalIgnoreCase;

    /// <summary>
    ///  <see cref="SongSyncService"/> クラスの新しいインスタンスを初期化します。
    /// </summary>
    /// <param name="config">設定情報を保持するオブジェクトです。</param>
    /// <param name="configPath">設定ファイルのパスを保持する文字列です。</param>
    public SongSyncService(
        AppConfig config,
        string configPath)
    {
        _config = config;
        _configPath = configPath;
    }

    /// <summary>
    /// 前回の実行で残った、所有を確認できる一時フォルダを回収します。
    /// </summary>
    public void RecoverStaleStaging()
    {
        if (string.IsNullOrWhiteSpace(_config.SoundartPath))
        {
            return;
        }

        try
        {
            string targetRoot = Path.GetFullPath(_config.SoundartPath);
            if (Directory.Exists(targetRoot))
            {
                RecoverStaleStagingDirectories(targetRoot);
            }
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            Console.WriteLine($"[警告]一時フォルダの回収先が無効です: {ex.Message}");
        }
    }

    public void Sync()
    {
        if (string.IsNullOrWhiteSpace(_config.BeatSaberPath) ||
            string.IsNullOrWhiteSpace(_config.SoundartPath))
        {
            throw new InvalidOperationException("同期する前に初期設定で両方のパスを指定してください。");
        }

        string sourceRoot = 
            Path.GetFullPath(_config.BeatSaberPath);

        string targetRoot =
            Path.GetFullPath(_config.SoundartPath);

        ValidatePaths(sourceRoot, targetRoot);

        if (!Directory.Exists(sourceRoot))
        {
            throw new DirectoryNotFoundException(
                $"Beat Saberのフォルダが存在しません: {sourceRoot}");
        }

        Directory.CreateDirectory(targetRoot);

        RecoverStaleStagingDirectories(targetRoot);

        Console.WriteLine($"曲を検索しています...");

        var desired = new Dictionary<string, string>(this._comparer);

        int skipped = 0;
        int scanned = 0;

        foreach (var songDir in Directory.EnumerateDirectories(sourceRoot))
        {
            scanned++;
            if (scanned % 500 == 0)
            {
                Console.WriteLine($"[検索中] {scanned} フォルダを確認しました。");
            }

            string? audioFile = FindAudiofile(songDir);

            if (audioFile is null)
            {
                skipped++;
                continue;
            }

            string extension =
                Path.GetExtension(audioFile);

            string foldername = 
                Path.GetFileName(
                    Path.TrimEndingDirectorySeparator(songDir));

            if (extension.StartsWith(
                    ".egg", 
                    StringComparison.OrdinalIgnoreCase))
            {
                extension = ".ogg";
            }

            string linkPath = 
                Path.Combine(targetRoot, foldername + extension);

            if (desired.ContainsKey(linkPath))
            {
                Console.WriteLine($"[警告] {songDir} のリンク先が重複しています。");
                skipped++;
                continue;
            }

            desired.Add(linkPath, Path.GetFullPath(audioFile));

        }

        Console.WriteLine($"[検索完了] {scanned} フォルダ、{desired.Count} 曲を確認しました。");

        var records = CreateRecordMap();

        int added = 0;
        int removed = 0;
        int unchanged = 0;
        int pendingChanges = 0;

        void Checkpoint(bool force = false)
        {
            if (pendingChanges == 0 || (!force && pendingChanges < 100))
            {
                return;
            }

            SaveRecords(records);
            pendingChanges = 0;
        }

        try
        {
            int checkedRecords = 0;
            int totalRecords = records.Count;
            foreach (var (link, record) in records.ToArray())
            {
                checkedRecords++;
                if (checkedRecords % 500 == 0)
                {
                    Console.WriteLine($"[照合中] {checkedRecords}/{totalRecords} 件の登録を確認しました。");
                }

                string oldSource = Path.GetFullPath(record.SourcePath);
                bool keep = desired.TryGetValue(link, out string? newSource)
                    && _comparer.Equals(oldSource, newSource);

                if (keep)
                {
                    continue;
                }

                if (DeleteOwnerLink(record))
                {
                    removed++;
                    records.Remove(link);
                    pendingChanges++;
                }
                else if (!PathExists(link))
                {
                    records.Remove(link);
                    pendingChanges++;
                }
                else if (!IsExpectedLink(link, oldSource))
                {
                    Console.WriteLine($"管理対象が変更されています: {link}");
                    records.Remove(link);
                    pendingChanges++;
                }

                Checkpoint();
            }

            var toCreate = new List<(string Link, string Source)>();
            int processed = 0;
            foreach (var (link, source) in desired)
            {
                processed++;
                if (processed % 500 == 0)
                {
                    Console.WriteLine($"[追加準備中] {processed}/{desired.Count} 曲を確認しました。");
                }

                if (records.ContainsKey(link) && IsExpectedLink(link, source))
                {
                    unchanged++;
                    continue;
                }

                // 通常ファイルや他ツールのリンクを上書きしない
                if (PathExists(link))
                {
                    if (records.Remove(link))
                    {
                        pendingChanges++;
                        Checkpoint();
                    }

                    Console.WriteLine($"[保護] {link}");
                    skipped++;
                    continue;
                }

                records[link] = new LinkRecord
                {
                    LinkPath = link,
                    SourcePath = source
                };
                toCreate.Add((link, source));
            }

            if (toCreate.Count > 0)
            {
                var staging = CreateStagingDirectory(targetRoot, toCreate);
                string stagingRoot = staging.Root;
                try
                {
                    // 予定を先に保存し、途中終了後の再同期でも未作成リンクを復旧できるようにする。
                    SaveRecords(records);
                    pendingChanges = 0;

                    for (int i = 0; i < toCreate.Count; i++)
                    {
                        var (link, source) = toCreate[i];
                        string stagedLink = Path.Combine(stagingRoot, Path.GetFileName(link));
                        if ((i + 1) % 500 == 0)
                        {
                            Console.WriteLine($"[公開中] {i + 1}/{toCreate.Count} 件を処理しました。");
                        }

                        bool created = false;
                        try
                        {
                            File.CreateSymbolicLink(stagedLink, source);
                            created = true;

                            if (!File.Exists(source) || !IsExpectedLink(stagedLink, source))
                            {
                                throw new IOException($"一時リンクの検証に失敗しました: {stagedLink}");
                            }

                            // 移動先は上書きしない。公開先に途中で別のファイルができても保護する。
                            File.Move(stagedLink, link);
                            added++;
                        }
                        catch (Exception ex) when (!created &&
                            (ex is UnauthorizedAccessException ||
                             ex.HResult == unchecked((int)0x80070522)))
                        {
                            for (int remaining = i; remaining < toCreate.Count; remaining++)
                            {
                                records.Remove(toCreate[remaining].Link);
                                pendingChanges++;
                            }

                            throw new InvalidOperationException(
                                "シンボリックリンクを作成する権限がありません。Windows の開発者モードを有効にするか、管理者権限で実行してください。",
                                ex);
                        }
                        catch (Exception ex)
                        {
                            records.Remove(link);
                            pendingChanges++;
                            Checkpoint();
                            Console.WriteLine($"[追加失敗] {link}: {ex.Message}");
                            skipped++;
                        }
                        finally
                        {
                            CleanupStagedLink(stagedLink);
                        }
                    }
                }
                finally
                {
                    staging.Lock.Dispose();
                    TryDeleteStagingDirectory(stagingRoot);
                }
            }
        }
        finally
        {
            Checkpoint(force: true);
        }

        Console.WriteLine($"[同期完了] {added} 件のリンクを追加、{removed} 件のリンクを削除、{unchanged} 件のリンクは変更なし、{skipped} 件をスキップしました。");
    }

    /// <summary>
    /// すべてのリンクを解除します。
    /// </summary>
    public void UnlinkAll()
    {
        int removed = 0;
        int pendingChanges = 0;
        var records = CreateRecordMap();

        try
        {
            int processed = 0;
            int totalRecords = records.Count;
            foreach (var (link, record) in records.ToArray())
            {
                processed++;
                if (processed % 500 == 0)
                {
                    Console.WriteLine($"[解除中] {processed}/{totalRecords} 件を確認しました。");
                }

                string source = Path.GetFullPath(record.SourcePath);

                if (DeleteOwnerLink(record) ||
                    !PathExists(link) ||
                    !IsExpectedLink(link, source))
                {
                    removed++;
                    records.Remove(link);
                    pendingChanges++;
                }

                if (pendingChanges >= 100)
                {
                    SaveRecords(records);
                    pendingChanges = 0;
                }
            }
        }
        finally
        {
            if (pendingChanges > 0)
            {
                SaveRecords(records);
            }
        }

        Console.WriteLine($"[解除完了] {removed} 件のリンクを解除しました。");
    }

    private static (string Root, FileStream Lock) CreateStagingDirectory(
        string targetRoot,
        IReadOnlyList<(string Link, string Source)> plannedLinks)
    {
        if (new DirectoryInfo(targetRoot).Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidOperationException(
                "公開先がシンボリックリンクまたはジャンクションです。同じドライブ内で安全に一時フォルダを作るため、実体のフォルダを指定してください。");
        }

        string parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(targetRoot))
            ?? throw new InvalidOperationException("公開先の親フォルダを特定できません。");
        string stagingRoot = Path.Combine(
            parent,
            $"{StagingPrefix}{Guid.NewGuid():N}");

        Directory.CreateDirectory(stagingRoot);
        FileStream? stagingLock = null;

        try
        {
            stagingLock = new FileStream(
                Path.Combine(stagingRoot, StagingLockName),
                FileMode.CreateNew,
                FileAccess.ReadWrite,
                FileShare.None);

            var manifest = new StagingManifest
            {
                TargetRoot = targetRoot,
                Links = plannedLinks.ToDictionary(
                    item => Path.GetFileName(item.Link),
                    item => item.Source,
                    StringComparer.OrdinalIgnoreCase)
            };

            File.WriteAllText(
                Path.Combine(stagingRoot, StagingManifestName),
                JsonSerializer.Serialize(manifest));

            return (stagingRoot, stagingLock);
        }
        catch
        {
            stagingLock?.Dispose();
            TryDeleteStagingDirectory(stagingRoot);
            throw;
        }
    }

    private void RecoverStaleStagingDirectories(string targetRoot)
    {
        string? parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(targetRoot));
        if (parent is null)
        {
            return;
        }

        try
        {
            foreach (string directory in Directory.EnumerateDirectories(
                parent, $"{StagingPrefix}*", SearchOption.TopDirectoryOnly))
            {
                try
                {
                    RecoverStaleStagingDirectory(directory, parent, targetRoot);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[警告]一時フォルダを回収できませんでした: {directory}: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[警告]一時フォルダを検索できませんでした: {parent}: {ex.Message}");
        }
    }

    private void RecoverStaleStagingDirectory(
        string directory,
        string parent,
        string targetRoot)
    {
        string name = Path.GetFileName(directory);
        if (!name.StartsWith(StagingPrefix, StringComparison.OrdinalIgnoreCase) ||
            !Guid.TryParseExact(name[StagingPrefix.Length..], "N", out _) ||
            !_comparer.Equals(Path.GetDirectoryName(Path.GetFullPath(directory)), parent) ||
            new DirectoryInfo(directory).Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            return;
        }

        string lockPath = Path.Combine(directory, StagingLockName);
        string manifestPath = Path.Combine(directory, StagingManifestName);
        if (!File.Exists(lockPath) || !File.Exists(manifestPath) ||
            File.GetAttributes(lockPath).HasFlag(FileAttributes.ReparsePoint) ||
            File.GetAttributes(manifestPath).HasFlag(FileAttributes.ReparsePoint))
        {
            return;
        }

        FileStream stagingLock;
        try
        {
            stagingLock = new FileStream(
                lockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
            // 別の同期処理が使用中。
            return;
        }

        bool recovered = false;
        using (stagingLock)
        {
            var manifest = JsonSerializer.Deserialize<StagingManifest>(
                File.ReadAllText(manifestPath));

            if (manifest is null || manifest.Version != 1 ||
                !_comparer.Equals(Path.GetFullPath(manifest.TargetRoot), targetRoot) ||
                manifest.Links is null)
            {
                return;
            }

            var allowedNames = new HashSet<string>(_comparer)
            {
                StagingLockName,
                StagingManifestName
            };

            foreach (var (fileName, source) in manifest.Links)
            {
                if (string.IsNullOrWhiteSpace(fileName) ||
                    fileName != Path.GetFileName(fileName) ||
                    Path.IsPathRooted(fileName) ||
                    fileName is "." or ".." ||
                    fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                    !allowedNames.Add(fileName) ||
                    string.IsNullOrWhiteSpace(source) ||
                    !Path.IsPathFullyQualified(source))
                {
                    return;
                }

                string stagedLink = Path.Combine(directory, fileName);
                if (PathExists(stagedLink) &&
                    (File.GetAttributes(stagedLink).HasFlag(FileAttributes.Directory) ||
                     !IsExpectedLink(stagedLink, source)))
                {
                    return;
                }
            }

            if (Directory.EnumerateFileSystemEntries(directory).Any(
                entry => !allowedNames.Contains(Path.GetFileName(entry))))
            {
                return;
            }

            foreach (var (fileName, source) in manifest.Links)
            {
                string stagedLink = Path.Combine(directory, fileName);
                if (PathExists(stagedLink))
                {
                    if (!IsExpectedLink(stagedLink, source))
                    {
                        return;
                    }

                    File.Delete(stagedLink);
                }
            }

            recovered = true;
        }

        if (recovered)
        {
            if (TryDeleteStagingDirectory(directory))
            {
                Console.WriteLine($"[回収] 前回の一時フォルダを片付けました: {directory}");
            }
        }
    }

    private static bool TryDeleteStagingDirectory(string stagingRoot)
    {
        try
        {
            if (!Directory.Exists(stagingRoot))
            {
                return true;
            }

            if (Directory.EnumerateFileSystemEntries(stagingRoot).Any(entry =>
                Path.GetFileName(entry) is not (StagingLockName or StagingManifestName)))
            {
                Console.WriteLine($"[警告]未確認の内容がある一時フォルダを残しました: {stagingRoot}");
                return false;
            }

            File.Delete(Path.Combine(stagingRoot, StagingManifestName));
            File.Delete(Path.Combine(stagingRoot, StagingLockName));
            Directory.Delete(stagingRoot);
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[警告]一時フォルダを削除できませんでした: {stagingRoot}: {ex.Message}");
            return false;
        }
    }

    private static void CleanupStagedLink(string stagedLink)
    {
        try
        {
            if (!PathExists(stagedLink))
            {
                return;
            }

            // 一時フォルダ内でも、通常ファイルに変わっていた場合は削除しない。
            if (!File.GetAttributes(stagedLink).HasFlag(FileAttributes.ReparsePoint))
            {
                Console.WriteLine($"[警告]一時フォルダ内の通常ファイルを残しました: {stagedLink}");
                return;
            }

            File.Delete(stagedLink);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[警告]一時リンクを削除できませんでした: {stagedLink}: {ex.Message}");
        }
    }

    private Dictionary<string, LinkRecord> CreateRecordMap()
    {
        var records = new Dictionary<string, LinkRecord>(_comparer);
        foreach (var record in _config.Links)
        {
            records[Path.GetFullPath(record.LinkPath)] = record;
        }

        return records;
    }

    private void SaveRecords(Dictionary<string, LinkRecord> records)
    {
        _config.Links = [.. records.Values];
        Save();
    }

    /// <summary>
    /// 指定されたリンクレコードに基づいて、リンクを解除します。
    /// </summary>
    /// <param name="record">リンクを解除する対象のリンクレコードです。</param>
    /// <returns>リンクの解除に成功した場合は true、それ以外の場合は false を返します。</returns>
    private bool DeleteOwnerLink(LinkRecord record)
    {
        string link = Path.GetFullPath(record.LinkPath);

        string source = Path.GetFullPath(record.SourcePath);

        if (!IsExpectedLink(link, source))
        {
            return false;
        }


        try
        {
            File.Delete(link);

            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[解除失敗] {link}: {ex.Message}");

            return false;
        }
    }

    /// <summary>
    /// 指定されたリンクパスが、指定されたソースパスに対応する期待されるリンクであるかどうかを確認します。
    /// </summary>
    /// <param name="linkPath">リンクパスを指定します。</param>
    /// <param name="sourcePath">ソースパスを指定します。</param>
    /// <returns>期待されるリンクである場合は true、それ以外の場合は false を返します。</returns>
    private bool IsExpectedLink(
        string linkPath,
        string sourcePath)
    {
        try
        {
            var info = new FileInfo(linkPath);

            if (!info.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                return false;
            }

            string? target = info.LinkTarget;

            if (target is null)
            {
                return false;
            }

            string resolved = 
                Path.IsPathRooted(target)
                    ? Path.GetFullPath(target)
                    : Path.GetFullPath(
                        Path.Combine(
                            Path.GetDirectoryName(linkPath)!, 
                            target));

            return 
                this._comparer
                    .Equals(
                        resolved, 
                        Path.GetFullPath(sourcePath));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 指定されたフォルダ内の音声ファイルを検索します。
    /// </summary>
    /// <param name="songDirectory">音声ファイルを検索するフォルダのパスです。</param>
    /// <returns>見つかった音声ファイルのパス、見つからなかった場合は null を返します。</returns>
    private static string? FindAudiofile(string songDirectory)
    {
        string? infoPath =
            Directory.EnumerateFiles(songDirectory)
                     .FirstOrDefault(x => 
                        Path.GetFileName(x)
                            .Equals(
                                "info.dat",
                                StringComparison.OrdinalIgnoreCase));

        if (infoPath is not null)
        {
            try
            {
                using var doc =
                    JsonDocument.Parse(
                        File.ReadAllText(infoPath));

                JsonElement root = doc.RootElement;

                string? filename = null;

                if (root.TryGetProperty(
                    "_songFilename",
                    out var oldName))
                {
                    filename = oldName.GetString();
                }
                else if (root.TryGetProperty(
                    "songFilename",
                    out var newName))
                {
                    filename = newName.GetString();
                }

                if (!string.IsNullOrWhiteSpace(filename))
                {
                    string candidate = 
                        Path.GetFullPath(
                            Path.Combine(songDirectory, filename));

                    string rootPath = 
                        Path.GetFullPath(songDirectory)
                            .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

                    if (candidate.StartsWith(rootPath, StringComparison.OrdinalIgnoreCase) &&
                        File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Info.dat警告] {songDirectory}: {ex.Message}");
            }
        }

        var files =
            Directory.EnumerateFiles(songDirectory)
                     .ToArray();

        return
            files.FirstOrDefault(x => 
                Path.GetExtension(x)
                    .Equals(".egg", StringComparison.OrdinalIgnoreCase))
            ?? files.FirstOrDefault(x =>
                Path.GetExtension(x)
                    .Equals(".ogg", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// 指定されたパスが存在するかどうかを確認します。
    /// </summary>
    /// <param name="path">確認するパスです。</param>
    /// <returns>パスが存在する場合は true、それ以外の場合は false を返します。</returns>
    private static bool PathExists(string path)
    {
        if (Path.Exists(path))
        {
            return true;
        }

        try
        {
            _ = File.GetAttributes(path);
            return true;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }
    }

    /// <summary>
    /// 指定された元フォルダとリンク先のパスを検証します。
    /// </summary>
    /// <param name="source">元フォルダのパスです。</param>
    /// <param name="target">リンク先フォルダのパスです。</param>
    /// <exception cref="InvalidOperationException"></exception>
    private static void ValidatePaths(string source, string target)
    {
        if (source.Equals(target, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("元フォルダとリンク先が同じです。");
        }

        string sourcePrefix = 
            Path.TrimEndingDirectorySeparator(source) + Path.DirectorySeparatorChar;

        string targetPrefix =
            Path.TrimEndingDirectorySeparator(target) + Path.DirectorySeparatorChar;

        if (target.StartsWith(sourcePrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"元フォルダとリンク先を親子関係にすることはできません: {source} -> {target}");
        }
    }

    /// <summary>
    /// 設定情報をJSON形式で保存します。
    /// </summary>
    private void Save()
    {
        string? directory = 
            Path.GetDirectoryName(this._configPath);

        if (directory is not null)
        {
            Directory.CreateDirectory(directory);
        }

        string json =
            System.Text.Json.JsonSerializer.Serialize(
                this._config,
                new System.Text.Json.JsonSerializerOptions
                {
                    WriteIndented = true
                });

        string temp = this._configPath + ".tmp";

        File.WriteAllText(temp, json);

        File.Move(temp, this._configPath, true);
    }


}
