using SoundartSongLinker.Models;

namespace SoundartSongLinker.Services;

/// <summary>
/// Steamライブラリの情報を取得するサービスクラスです。
/// </summary>
public sealed class SteamLibraryService
{
    /// <summary>
    /// Beat SaberのSteam AppIdを表す定数です。
    /// </summary>
    private readonly string BeatSaberAppId = "620980";

    /// <summary>
    /// Steamライブラリのパスを比較するための文字列比較子です。
    /// </summary>
    private static readonly StringComparer PathComparer =
        StringComparer.OrdinalIgnoreCase;

    /// <summary>
    /// Steamのインストールパスを取得します。
    /// </summary>
    /// <returns>Steamのインストールパス、見つからない場合はnull</returns>
    public string? FindSteamPath()
    {
        string[] registryKeys =
        [
            @"HKEY_CURRENT_USER\SOFTWARE\Valve\Steam",
            @"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam",
            @"HKEY_LOCAL_MACHINE\SOFTWARE\Valve\Steam"
        ];

        foreach (string key in registryKeys)
        {
            foreach (string valueName in new[] { "SteamPath", "InstallPath" })
            {
                object? value =
                    Microsoft.Win32.Registry.GetValue(
                        key,
                        valueName,
                        null);

                if (value is not string path)
                {
                    continue;
                }

                path = Path.GetFullPath(path);

                if (Directory.Exists(path) &&
                    Directory.Exists(Path.Combine(path, "steamapps")))
                {
                    return path;
                }
            }
        }

        string[] candidates =
            [
                Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                    "Steam"),
                Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                    "Steam")

            ];

        return
            candidates.FirstOrDefault(Directory.Exists);
    }

    /// <summary>
    /// Steamライブラリのパスを取得します。
    /// </summary>
    /// <returns>Steamライブラリのパスのリスト</returns>
    public IReadOnlyList<string> GetSteamLibraries()
    {
        string? steamPath = FindSteamPath();

        if (steamPath is null)
        {
            return [];
        }

        var libraries = new HashSet<string>(PathComparer);

        libraries.Add(Path.GetFullPath(steamPath));

        string vdfPath =
            Path.Combine(
                steamPath,
                "steamapps",
                "libraryfolders.vdf");

        if (!File.Exists(vdfPath))
        {
            return libraries.ToList();
        }

        var root =
            SteamVdfParser.ParseFile(vdfPath);

        var folders =
            SteamVdfParser.GetObject(root, "libraryfolders");

        if (folders is null)
        {
            return libraries.ToList();
        }

        foreach (var entry in folders)
        {
            // ライブラリ番号以外は対象外
            if (!int.TryParse(entry.Key, out _))
            {
                continue;
            }

            string? path = null;

            // 新しい形式
            if (entry.Value is Dictionary<string, object> folder)
            {
                path = SteamVdfParser.GetString(folder, "path");
            }
            // 古い形式
            else if (entry.Value is string str)
            {
                path = str;
            }

            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            try
            {
                path = Path.GetFullPath(path);

                if (Directory.Exists(path))
                {
                    libraries.Add(path);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[警告]ライブラリパス: {path}, エラー: {ex.Message}");
            }
        }

        return libraries.ToList();
    }

    /// <summary>
    /// インストールされているSteamゲームのリストを取得します。
    /// </summary>
    /// <returns>インストールされているSteamゲームのリスト</returns>
    public IReadOnlyList<SteamGame> GetInstalledGames()
    {
        var games = new List<SteamGame>();

        foreach (string library in GetSteamLibraries())
        {
            string steamapps = Path.Combine(library, "steamapps");
            if (!Directory.Exists(steamapps))
            {
                continue;
            }

            foreach (string manifest in Directory.EnumerateFiles(steamapps, "appmanifest_*.acf"))
            {
                try
                {
                    var game = ParseManifest(manifest, library);

                    if (game is not null)
                    {
                        games.Add(game);
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[警告]マニフェストファイル: {manifest}, エラー: {ex.Message}");
                }
            }
        }

        return
            [.. games];
    }

    /// <summary>
    /// 指定されたマニフェストファイルを解析し、SteamGameオブジェクトを作成します。
    /// </summary>
    /// <param name="manifest">マニフェストファイルのパス</param>
    /// <param name="library">ライブラリのパス</param>
    /// <returns>解析に成功した場合はSteamGameオブジェクト、失敗した場合はnull</returns>
    private static SteamGame? ParseManifest(string manifest, string library)
    {
        var root =
            SteamVdfParser.ParseFile(manifest);

        var app =
            SteamVdfParser.GetObject(root, "AppState");

        if (app is null)
        {
            return null;
        }

        string? appId =
            SteamVdfParser.GetString(app, "appid");

        string? name =
            SteamVdfParser.GetString(app, "name");

        string? installDir =
            SteamVdfParser.GetString(app, "installdir");

        if (string.IsNullOrWhiteSpace(appId) ||
            string.IsNullOrWhiteSpace(installDir))
        {
            return null;
        }

        if (Path.IsPathRooted(installDir) ||
            installDir is ".." or "." ||
            installDir.Contains("\\") ||
            installDir.Contains("/"))
        {
            return null;
        }

        string directory = Path.GetFullPath(
            Path.Combine(
                library,
                "steamapps",
                "common",
                installDir));

        if (!Directory.Exists(directory))
        {
            return null;
        }

        return new SteamGame
        {
            AppId = appId,
            Name = name,
            InstallDirectory = installDir,
            LibraryPath = library
        };
    }

    /// <summary>
    /// 指定されたAppIdまたは名前に一致するSteamGameを検索します。
    /// </summary>
    /// <param name="appId">検索するSteamゲームのAppId</param>
    /// <param name="name">検索するSteamゲームの名前</param>
    /// <returns>一致するSteamGameオブジェクト、見つからない場合はnull</returns>
    public SteamGame? FindGame(
        string? appId = null,
        string? name = null)
    {
        var games = GetInstalledGames();

        if (!string.IsNullOrWhiteSpace(appId))
        {
            var game = 
                games.FirstOrDefault(
                    g => g.AppId.Equals(appId, StringComparison.OrdinalIgnoreCase));

            if (game is not null)
            {
                return game;
            }
        }

        if (!string.IsNullOrWhiteSpace(name))
        {
            var game = 
                games.FirstOrDefault(
                    g => g.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

            if (game is not null)
            {
                return game;
            }
        }

        return null;
    }

    /// <summary>
    /// Beat SaberのSteamゲームを検索します。
    /// </summary>
    /// <returns>一致するSteamGameオブジェクト、見つからない場合はnull</returns>
    public SteamGame? FindBeatSaber()
    {
        return 
            FindGame(
                appId: BeatSaberAppId, 
                name: "Beat Saber");
    }

    /// <summary>
    /// SoundartのSteamゲームを検索します。
    /// </summary>
    /// <returns>一致するSteamGameオブジェクト、見つからない場合はnull</returns>
    public SteamGame? FindSoundart()
    {
        return
            FindGame(
                name: "Soundart");
    }
}
