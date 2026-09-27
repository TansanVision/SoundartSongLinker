using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace SoundartSongLinker.Services;

/// <summary>
/// SteamのVDF形式のファイルを解析するためのクラスです。
/// </summary>
public sealed class SteamVdfParser
{
    /// <summary>
    /// 指定されたファイルを解析し、ネストされたオブジェクトを含む辞書を返します。
    /// </summary>
    /// <param name="path">解析するファイルのパス</param>
    /// <returns>解析された辞書</returns>
    public static Dictionary<string, object> ParseFile(string path)
    {
        string content = File.ReadAllText(path);

        return Parse(content);
    }

    /// <summary>
    /// 指定された文字列を解析し、ネストされたオブジェクトを含む辞書を返します。
    /// </summary>
    /// <param name="content">解析する文字列</param>
    /// <returns>解析された辞書</returns>
    public static Dictionary<string, object> Parse(string content)
    {
        var tokens = Tokenize(content);

        int index = 0;

        return ParseObject(tokens, ref index, false);
    }

    /// <summary>
    /// トークン化された文字列のリストを解析し、ネストされたオブジェクトを含む辞書を返します。
    /// </summary>
    /// <param name="tokens">トークン化された文字列のリスト</param>
    /// <param name="index">現在のインデックス</param>
    /// <param name="nested">ネストされたオブジェクトかどうかを示すフラグ</param>
    /// <returns>解析された辞書</returns>
    /// <exception cref="FormatException"></exception>
    public static Dictionary<string, object> ParseObject(
        List<string> tokens,
        ref int index,
        bool nested)
    {
        var result =
            new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

        while (index < tokens.Count)
        {
            string key = tokens[index++];
            if (key == "}")
            {
                if (!nested)
                {
                    throw new FormatException("予期しない閉じ中括弧です。");
                }
                return result;
            }


            if (key == "{")
            {
                throw new FormatException("予期しない開き中括弧です。");
            }

            if (index >= tokens.Count)
            {
                throw new FormatException("無効な VDF 形式です。");
            }

            string value = tokens[index++];

            if (value == "{")
            {
                result[key] = ParseObject(tokens, ref index, true);
            }
            else if (value == "}")
            {
                throw new FormatException("無効な VDF 形式です。");
            }
            else
            {
                result[key] = value;
            }
        }

        if (nested)
        {
            throw new FormatException("閉じ中括弧が不足しています。");
        }

        return result;
    }

    /// <summary>
    /// 指定された文字列をトークン化し、トークンのリストを返します。
    /// </summary>
    /// <param name="content">トークン化する文字列</param>
    /// <returns>トークンのリスト</returns>
    private static List<string> Tokenize(string content)
    {
        var tokens = new List<string>();

        var regex =
            new Regex("\"(?:\\\\.|[^\"\\\\])*\"|//[^\\r\\n]*|[{}]|[^\\s{}\"]+", RegexOptions.Compiled);

        foreach (Match match in regex.Matches(content))
        {
            string token = match.Value;

            if (token.StartsWith("//"))
            {
                // コメントを無視
                continue;
            }

            if (token.StartsWith('"'))
            {
                var sb = new StringBuilder();

                for (int i = 1; i < token.Length - 1; i++)
                {
                    if (token[i] == '\\' &&
                        i + 1 < token.Length - 1)
                    {
                        char next = token[++i];

                        sb.Append(next switch
                        {
                            '"' => '"',
                            '\\' => '\\',
                            'n' => '\n',
                            'r' => '\r',
                            't' => '\t',
                            _ => "\\" + next
                        });
                    }
                    else
                    {
                        sb.Append(token[i]);
                    }
                }

                tokens.Add(sb.ToString());
            }
            else
            {
                tokens.Add(token);
            }
        }

        return tokens;
    }

    /// <summary>
    /// 指定されたキーに対応するオブジェクトを取得します。
    /// </summary>
    /// <param name="source">検索対象のディクショナリ</param>
    /// <param name="key">取得するオブジェクトのキー</param>
    /// <returns>指定されたキーに対応するオブジェクト、存在しない場合は null</returns>
    public static Dictionary<string, object>? GetObject(
        Dictionary<string, object> source,
        string key)
    {
        return 
            source.TryGetValue(key, out object? value) ?
                value as Dictionary<string, object>
                : null;
    }

    /// <summary>
    /// 指定されたキーに対応する文字列を取得します。
    /// </summary>
    /// <param name="source">検索対象のディクショナリ</param>
    /// <param name="key">取得する文字列のキー</param>
    /// <returns>指定されたキーに対応する文字列、存在しない場合は null</returns>
    public static string? GetString(
        Dictionary<string, object> source,
        string key)
    {
        return
            source.TryGetValue(key, out object? value) ?
                value as string
                : null;
    }
}
