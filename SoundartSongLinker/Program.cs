using SoundartSongLinker.Models;
using SoundartSongLinker.Services;
using Spectre.Console;
using System.Text.Json;

namespace SoundartSongLinker;

/// <summary>
/// アプリケーションのエントリーポイントを表すクラスです。
/// </summary>
internal class Program
{
    /// <summary>
    /// アプリケーションの設定情報を保持する静的フィールドです。
    /// </summary>
    private static AppConfig? config;

    /// <summary>
    /// アプリケーションの設定ファイルのパスを保持する静的フィールドです。
    /// </summary>
    private static string? configPath;

    /// <summary>
    /// Steamライブラリサービスのインスタンスを保持する静的フィールドです。
    /// </summary>
    private static SteamLibraryService? steamService;

    /// <summary>
    /// アプリケーションのエントリーポイントです。
    /// </summary>
    /// <param name="args">コマンドライン引数を指定します。</param>
    static void Main(string[] args)
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.WriteLine("このアプリケーションはWindowsでのみ動作します。");
            return;
        }

        string configDirectory =
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "SoundartSongLinker");

        configPath = Path.Combine(configDirectory, "config.json");

        Directory.CreateDirectory(configDirectory);

        config =
            File.Exists(configPath)
            ? JsonSerializer.Deserialize<AppConfig>(
                File.ReadAllText(configPath)) ?? new AppConfig()
            : new();

        var service =
            new SongSyncService(
                config,
                configPath);

        service.RecoverStaleStaging();

        steamService =
            new SteamLibraryService();

        while (true)
        {
            var menu = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("[underline green]メニューを選択してください:[/]")
                    .AddChoices(["初期設定 / パス変更", "差分同期", "リンク解除", "Steamライブラリ情報表示", "終了"]));

            try
            {

                switch (menu)
                {
                    case "初期設定 / パス変更":
                        AnsiConsole.Clear();
                        Setup();
                        break;
                    case "差分同期":
                        AnsiConsole.Clear();
                        service.Sync();
                        break;
                    case "リンク解除":
                        AnsiConsole.Clear();
                        service.UnlinkAll();
                        break;
                    case "Steamライブラリ情報表示":
                        DisplaySteamLibraryInfo();
                        break;
                    case "終了":
                    default:
                        return;
                }

            }
            catch (Exception ex)
            {
                Console.WriteLine($"エラーが発生しました: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// アプリケーションの初期設定を行います。
    /// </summary>
    private static void Setup()
    {
        if (config == null || configPath == null)
        {
            throw new InvalidOperationException("設定情報が初期化されていません。");
        }

        var steam = steamService ?? throw new InvalidOperationException("Steamライブラリサービスが初期化されていません。");
        var beatSaber = steam.FindBeatSaber();
        string defaultBeatSaberPath = Directory.Exists(config.BeatSaberPath)
            ? config.BeatSaberPath
            : string.Empty;

        if (beatSaber is not null)
        {
            string customLevels =
                Path.Combine(
                    beatSaber.InstallDirectory,
                    "Beat Saber_Data",
                    "CustomLevels");

            if (Directory.Exists(customLevels))
            {
                defaultBeatSaberPath = customLevels;
            }   
        }

        var soundart = steam.FindSoundart();
        string defaultSoundartPath = Directory.Exists(config.SoundartPath)
            ? config.SoundartPath
            : string.Empty;

        if (soundart is not null)
        {
            string soundartCustomLevelsLink =
                Path.Combine(
                    soundart.InstallDirectory,
                    "Beat Saber Custom Levels Link");

            if (Directory.Exists(soundartCustomLevelsLink))
            {
                defaultSoundartPath = soundartCustomLevelsLink;
            }
            else
            {
                Directory.CreateDirectory(soundartCustomLevelsLink);
                defaultSoundartPath = soundartCustomLevelsLink;
            }
        }

        AnsiConsole.Clear();
        AnsiConsole.MarkupLine("[underline green]Setup[/]");
        var beatSaberPath = AnsiConsole.Console.Prompt(
            new TextPrompt<string>("Beat Saberのインストールパスを入力してください:")
                .DefaultValue<string>(defaultBeatSaberPath)
                .Validate(path =>
                {
                    if (string.IsNullOrWhiteSpace(path))
                    {
                        return ValidationResult.Error("[red]パスは空にできません。[/]");
                    }
                    if (!Directory.Exists(path))
                    {
                        return ValidationResult.Error("[red]指定されたパスは存在しません。[/]");
                    }
                    return ValidationResult.Success();
                }));

        config.BeatSaberPath = beatSaberPath;

        var soundartPath = AnsiConsole.Console.Prompt(
            new TextPrompt<string>("Soundartのインストールパスを入力してください:")
                .DefaultValue<string>(defaultSoundartPath)
                .Validate(path =>
                {
                    if (string.IsNullOrWhiteSpace(path))
                    {
                        return ValidationResult.Error("[red]パスは空にできません。[/]");
                    }
                    if (!Directory.Exists(path))
                    {
                        return ValidationResult.Error("[red]指定されたパスは存在しません。[/]");
                    }
                    return ValidationResult.Success();
                }));
        config.SoundartPath = soundartPath;

        SaveConfig();
    }

    /// <summary>
    /// Steamライブラリの情報を表示します。
    /// </summary>
    /// <exception cref="InvalidOperationException">Steamライブラリサービスが初期化されていない場合にスローされます。</exception>
    private static void DisplaySteamLibraryInfo()
    {
        if (steamService == null)
        {
            throw new InvalidOperationException("Steamライブラリサービスが初期化されていません。");
        }

        var libraries = steamService.GetSteamLibraries();
        AnsiConsole.Clear();
        AnsiConsole.MarkupLine("[underline green]Steamライブラリ情報[/]");

        foreach (var library in libraries)
        {
            AnsiConsole.MarkupLine($"[yellow]ライブラリパス:[/] {library}");
            var games = steamService.GetInstalledGames().Where(g => g.LibraryPath == library).ToList();
            foreach (var game in games)
            {
                AnsiConsole.MarkupLine($"  [green]ゲーム名:[/] {game.Name}, [blue]AppId:[/] {game.AppId}, [magenta]インストールディレクトリ:[/] {game.InstallDirectory}");
            }
        }

        // 対象のゲーム beat saber と soundart の情報を表示
        var beatSaber = steamService.FindBeatSaber();
        var soundart = steamService.FindSoundart();

        if (beatSaber is not null)
        {
            AnsiConsole.MarkupLine($"  [green]Beat Saber:[/] {beatSaber.Name}, [blue]AppId:[/] {beatSaber.AppId}, [magenta]インストールディレクトリ:[/] {beatSaber.InstallDirectory}");
        }

        if (soundart is not null)
        {
            AnsiConsole.MarkupLine($"  [green]Soundart:[/] {soundart.Name}, [blue]AppId:[/] {soundart.AppId}, [magenta]インストールディレクトリ:[/] {soundart.InstallDirectory}");
        }
    }

    /// <summary>
    /// 指定されたパスに設定情報を保存します。
    /// </summary>
    private static void SaveConfig()
    {
        if (config == null || configPath == null)
        {
            throw new InvalidOperationException("設定情報が初期化されていません。");
        }

        string json = 
            JsonSerializer.Serialize(
                config, 
                new JsonSerializerOptions 
                { 
                    WriteIndented = true 
                });

        string temp = configPath + ".tmp";

        File.WriteAllText(temp, json);

        File.Move(temp, configPath, true);
    }
}
