namespace SoundartSongLinker.Models;

/// <summary>
/// アプリケーションの設定情報を表すクラスです。
/// </summary>
public sealed class AppConfig
{
    /// <summary>
    /// Beat Saberのインストールパスを取得または設定します。
    /// </summary>
    public string BeatSaberPath
    {
        get;
        set;
    }
    = string.Empty;

    /// <summary>
    /// Soundartのインストールパスを取得または設定します。
    /// </summary>
    public string SoundartPath
    {
        get;
        set;
    }
    = string.Empty;

    /// <summary>
    /// リンク情報の配列を取得または設定します。
    /// </summary>
    public LinkRecord[] Links
    {
        get;
        set;
    }
    = [];
}
