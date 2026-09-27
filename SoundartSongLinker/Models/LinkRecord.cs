namespace SoundartSongLinker.Models;

/// <summary>
/// リンク情報を表すクラスです。
/// </summary>
public sealed class LinkRecord
{
    /// <summary>
    /// ソースのパスを取得または設定します。
    /// </summary>
    public string SourcePath
    {
        get;
        set;
    }
    = string.Empty;

    /// <summary>
    /// リンク先のパスを取得または設定します。
    /// </summary>
    public string LinkPath
    {
        get;
        set;
    }
    = string.Empty;
}
