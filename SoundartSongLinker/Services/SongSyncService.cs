using SoundartSongLinker.Models;
using System.Text.Json;

namespace SoundartSongLinker.Services;

/// <summary>
/// アプリケーションの設定情報を管理し、リンク情報の同期を行うサービスクラスです。
/// </summary>
public sealed class SongSyncService
{
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

        Console.WriteLine($"曲を検索しています...");

        var desired = new Dictionary<string, string>(this._comparer);

        int skipped = 0;

        foreach (var songDir in Directory.EnumerateDirectories(sourceRoot))
        {
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

            int added = 0;
            int removed = 0;
            int unchanged = 0;

            foreach (var record in this._config.Links)
            {
                string link  = Path.GetFullPath(record.LinkPath);

                string oldSource = Path.GetFullPath(record.SourcePath);

                bool keep = 
                    desired.TryGetValue(
                        link, 
                        out string? newSource)
                    && this._comparer
                           .Equals(oldSource, newSource);

                if (keep)
                {
                    continue;
                }

                if (DeleteOwnerLink(record))
                {
                    removed++;
                    this._config.Links =
                        [.. this._config.Links.Where(r => r != record)];
                    this.Save();
                }
                else
                {
                    if (!PathExists(link))
                    {
                        this._config.Links =
                            [.. this._config.Links.Where(r => r != record)];
                        this.Save();
                    }
                    else if(!IsExpectedLink(link, oldSource))
                    {
                        Console.WriteLine($"管理対象が変更されています: {link}");

                        this._config.Links =
                            [.. this._config.Links.Where(r => r != record)];
                        this.Save();
                    }
                }
            }

            foreach (var item in desired)
            {
                string link = item.Key;
                string source = item.Value;

                LinkRecord? existing = 
                    this._config
                        .Links
                        .FirstOrDefault(x =>
                            this._comparer.Equals(
                                Path.GetFullPath(x.LinkPath),
                                link));

                // 既存の管理対象リンクが正しい場合
                if (existing is not null &&
                    IsExpectedLink(link, source))
                {
                    unchanged++;
                    continue;
                }

                // 管理対象リンクはあるがリンクが消えている
                if (existing is not null)
                {
                    if (PathExists(link))
                    {
                        Console.WriteLine($"[保護] {link}");
                        skipped++;
                        continue;
                    }

                    this._config.Links =
                        [.. this._config.Links.Where(r => r != existing)];
                    this.Save();
                }

                // 通常ファイルや他ツールのリンクを上書きしない
                if (PathExists(link))
                {
                    Console.WriteLine($"[スキップ] 既存ファイル：{link}");
                    skipped++;
                    continue;
                }

                try
                {
                    File.CreateSymbolicLink(link, source);

                    this._config.Links =
                        [.. this._config.Links, new LinkRecord
                        {
                            LinkPath = link,
                            SourcePath = source
                        }];

                    this.Save();

                    added++;

                    Console.WriteLine($"[追加] {link} -> {source}");

                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[追加失敗] {link}: {ex.Message}");
                }
            }

            Console.WriteLine($"[同期完了] {added} 件のリンクを追加、{removed} 件のリンクを削除、{unchanged} 件のリンクは変更なしでした。");
        }
    }

    /// <summary>
    /// すべてのリンクを解除します。
    /// </summary>
    public void UnlinkAll()
    {
        int removed = 0;

        foreach (var record in _config.Links)
        {
            string link = Path.GetFullPath(record.LinkPath);

            string source = Path.GetFullPath(record.SourcePath);

            if (DeleteOwnerLink(record))
            {
                removed++;

                this._config.Links = 
                    [.. this._config.Links.Where(r => r != record)];
                this.Save();
            }
            else if (!PathExists(link) || !IsExpectedLink(link, source))
            {
                removed++;
                this._config.Links =
                    [.. this._config.Links.Where(r => r != record)];
                this.Save();
            }
        }

        Console.WriteLine($"[解除完了] {removed} 件のリンクを解除しました。");
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

            Console.WriteLine($"[解除] {Path.GetFullPath(link)}");

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

                    if (candidate.StartsWith(rootPath, StringComparison.OrdinalIgnoreCase))
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
