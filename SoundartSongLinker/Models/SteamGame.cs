namespace SoundartSongLinker.Models;

/// <summary>
/// Steamゲームの情報を表すクラスです。
/// </summary>
public sealed class SteamGame
{
    /// <summary>
    /// SteamゲームのAppIdを取得または設定します。
    /// </summary>
    public required string AppId
    {
        get;
        init;
    }

    /// <summary>
    /// Steamゲームの名前を取得または設定します。
    /// </summary>
    public required string Name
    {
        get;
        init;
    }

    /// <summary>
    /// Steamゲームのインストールディレクトリを取得または設定します。
    /// </summary>
    public required string InstallDirectory
    {
        get;
        init;
    }

    /// <summary>
    /// Steamライブラリのパスを取得または設定します。
    /// </summary>
    public required string LibraryPath
    {
        get;
        init;
    }

    /// <summary>
    /// このクラスの文字列表現を返します。
    /// </summary>
    /// <returns>このクラスの文字列表現。</returns>
    public override string ToString()
    {
        return $"{Name} ({AppId}) - {InstallDirectory} in {LibraryPath}";
    }
}
