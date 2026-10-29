using System;
using System.Collections.Generic;
using System.Text;

namespace XIVShade.Effects;

public enum FxTokenKind
{
    Ident,
    Number,
    String,
    Punct,
    Eof,
}

/// <summary>A token plus the whitespace in front of it, so a token stream can be written back out unchanged.</summary>
public readonly record struct FxToken(FxTokenKind Kind, string Text, string Lead)
{
    public bool Is(string text) => Kind != FxTokenKind.String && Text == text;

    public override string ToString() => Lead + Text;
}

public sealed class FxException : Exception
{
    public FxException(string message) : base(message) { }
}

public static class FxLexer
{
    private static readonly string[] Punctuators =
    {
        "<<=", ">>=", "##", "::", "<<", ">>", "<=", ">=", "==", "!=", "&&", "||", "->", "+=", "-=", "*=", "/=", "%=", "&=", "|=", "^=", "++", "--",
    };

    public static List<FxToken> Tokenize(string text)
    {
        var tokens = new List<FxToken>();
        var i = 0;
        while (true)
        {
            var leadStart = i;
            while (i < text.Length && char.IsWhiteSpace(text[i]))
                i++;
            var lead = text.Substring(leadStart, i - leadStart);

            if (i >= text.Length)
            {
                tokens.Add(new FxToken(FxTokenKind.Eof, "", lead));
                return tokens;
            }

            var c = text[i];
            var start = i;
            if (IsIdentStart(c))
            {
                while (i < text.Length && IsIdentPart(text[i]))
                    i++;
                tokens.Add(new FxToken(FxTokenKind.Ident, text.Substring(start, i - start), lead));
            }
            else if (char.IsDigit(c) || (c == '.' && i + 1 < text.Length && char.IsDigit(text[i + 1])))
            {
                i++;
                while (i < text.Length)
                {
                    var d = text[i];
                    if (char.IsLetterOrDigit(d) || d == '.' || d == '_')
                        i++;
                    else if ((d == '+' || d == '-') && (text[i - 1] is 'e' or 'E') && !text.AsSpan(start, i - start).StartsWith("0x"))
                        i++;
                    else
                        break;
                }

                tokens.Add(new FxToken(FxTokenKind.Number, text.Substring(start, i - start), lead));
            }
            else if (c is '"' or '\'')
            {
                i++;
                while (i < text.Length && text[i] != c)
                {
                    if (text[i] == '\\' && i + 1 < text.Length)
                        i++;
                    i++;
                }

                i = Math.Min(i + 1, text.Length);
                tokens.Add(new FxToken(FxTokenKind.String, text.Substring(start, i - start), lead));
            }
            else
            {
                string? matched = null;
                foreach (var p in Punctuators)
                {
                    if (string.CompareOrdinal(text, i, p, 0, p.Length) == 0)
                    {
                        matched = p;
                        break;
                    }
                }

                matched ??= c.ToString();
                i += matched.Length;
                tokens.Add(new FxToken(FxTokenKind.Punct, matched, lead));
            }
        }
    }

    public static string Join(IEnumerable<FxToken> tokens)
    {
        var sb = new StringBuilder();
        foreach (var token in tokens)
            sb.Append(token.Lead).Append(token.Text);
        return sb.ToString();
    }

    private static bool IsIdentStart(char c) => char.IsLetter(c) || c == '_';

    private static bool IsIdentPart(char c) => char.IsLetterOrDigit(c) || c == '_';
}
