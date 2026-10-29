using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace XIVShade.Effects;

/// <summary>
/// A C preprocessor covering what ReShade effects use: #include, #pragma once, #define (object-like, function-like,
/// # and ##), #undef, conditionals with integer expressions, and #error.
/// </summary>
public sealed class FxPreprocessor
{
    private sealed record Macro(string[]? Parameters, List<FxToken> Body);

    private readonly Dictionary<string, Macro> macros = new();
    private readonly List<string> includeDirectories;
    private readonly HashSet<string> includedOnce = new(StringComparer.Ordinal);
    private readonly StringBuilder output = new();

    public FxPreprocessor(IEnumerable<string> includeDirectories, IReadOnlyDictionary<string, string> definitions)
    {
        this.includeDirectories = includeDirectories.ToList();
        foreach (var (name, value) in definitions)
            macros[name] = new Macro(null, FxLexer.Tokenize(value).Where(t => t.Kind != FxTokenKind.Eof).ToList());
    }

    /// <summary>Files read while processing, for change detection.</summary>
    public List<string> Dependencies { get; } = new();

    public string Process(string path)
    {
        output.Clear();
        ProcessFile(Path.GetFullPath(path), 0);
        return output.ToString();
    }

    private void ProcessFile(string path, int depth)
    {
        if (depth > 32)
            throw new FxException($"#include nesting too deep at {path}");

        if (includedOnce.Contains(path))
            return;

        Dependencies.Add(path);
        var lines = SplitLines(StripComments(File.ReadAllText(path)));

        // One entry per open #if: whether the enclosing block is active, whether a branch has been taken, whether this branch is active.
        var stack = new Stack<(bool Parent, bool Taken, bool Active)>();
        var active = true;

        for (var lineIndex = 0; lineIndex < lines.Count; lineIndex++)
        {
            var line = lines[lineIndex];
            var trimmed = line.TrimStart();
            if (!trimmed.StartsWith('#'))
            {
                if (!active)
                    continue;

                // A function-like macro call may continue on the following lines.
                while (true)
                {
                    try
                    {
                        output.Append(FxLexer.Join(Expand(Strip(FxLexer.Tokenize(line)), new HashSet<string>()))).Append('\n');
                        break;
                    }
                    catch (FxException ex) when (ex.Message.StartsWith("Unterminated macro call") && lineIndex + 1 < lines.Count)
                    {
                        line += " " + lines[++lineIndex];
                    }
                }

                continue;
            }

            var rest = trimmed.Substring(1).TrimStart();
            var nameEnd = 0;
            while (nameEnd < rest.Length && (char.IsLetter(rest[nameEnd]) || rest[nameEnd] == '_'))
                nameEnd++;
            var directive = rest.Substring(0, nameEnd);
            var argument = rest.Substring(nameEnd).Trim();

            switch (directive)
            {
                case "if":
                case "ifdef":
                case "ifndef":
                {
                    var condition = active && directive switch
                    {
                        "ifdef" => macros.ContainsKey(argument),
                        "ifndef" => !macros.ContainsKey(argument),
                        _ => Evaluate(argument),
                    };
                    stack.Push((active, condition, condition));
                    active = active && condition;
                    break;
                }
                case "elif":
                {
                    var (parent, taken, _) = stack.Pop();
                    var condition = parent && !taken && Evaluate(argument);
                    stack.Push((parent, taken || condition, condition));
                    active = parent && condition;
                    break;
                }
                case "else":
                {
                    var (parent, taken, _) = stack.Pop();
                    var condition = parent && !taken;
                    stack.Push((parent, true, condition));
                    active = parent && condition;
                    break;
                }
                case "endif":
                    if (stack.Count == 0)
                        throw new FxException($"{path}: #endif without #if");
                    active = stack.Pop().Parent;
                    break;
                default:
                    if (!active)
                        break;
                    Directive(directive, argument, path, depth);
                    break;
            }
        }
    }

    private void Directive(string directive, string argument, string currentFile, int depth)
    {
        switch (directive)
        {
            case "define":
                Define(argument);
                break;
            case "undef":
                macros.Remove(argument);
                break;
            case "include":
            {
                var trimmed = argument;
                if (trimmed.Length < 2 || (trimmed[0] != '"' && trimmed[0] != '<'))
                {
                    // The name may come from a macro.
                    trimmed = FxLexer.Join(Expand(Strip(FxLexer.Tokenize(argument)), new HashSet<string>())).Trim();
                }

                var name = trimmed.Substring(1, trimmed.Length - 2);
                ProcessFile(ResolveInclude(name, trimmed[0] == '<', currentFile), depth + 1);
                break;
            }
            case "pragma":
                if (argument == "once")
                    includedOnce.Add(currentFile);
                break;
            case "error":
                throw new FxException($"{Path.GetFileName(currentFile)}: #error {argument}");
        }
    }

    private string ResolveInclude(string name, bool system, string currentFile)
    {
        var candidates = new List<string>();
        if (!system)
            candidates.Add(Path.Combine(Path.GetDirectoryName(currentFile)!, name));
        candidates.AddRange(includeDirectories.Select(d => Path.Combine(d, name)));
        if (system)
            candidates.Add(Path.Combine(Path.GetDirectoryName(currentFile)!, name));

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
                return Path.GetFullPath(candidate);
        }

        throw new FxException($"Cannot find include '{name}' (from {Path.GetFileName(currentFile)})");
    }

    private void Define(string argument)
    {
        var tokens = FxLexer.Tokenize(argument);
        if (tokens[0].Kind != FxTokenKind.Ident)
            throw new FxException($"Malformed #define: {argument}");

        var name = tokens[0].Text;
        var index = 1;
        string[]? parameters = null;

        // A '(' directly after the name, with no space, starts a parameter list.
        if (tokens[index].Is("(") && tokens[index].Lead.Length == 0)
        {
            var list = new List<string>();
            index++;
            while (!tokens[index].Is(")"))
            {
                if (tokens[index].Kind == FxTokenKind.Ident)
                    list.Add(tokens[index].Text);
                else if (tokens[index].Is("."))
                {
                    // "..." arrives as three '.' tokens
                    list.Add("__VA_ARGS__");
                    index += 2;
                }

                index++;
                if (tokens[index - 1].Kind == FxTokenKind.Eof)
                    throw new FxException($"Unterminated macro parameters: {argument}");
            }

            index++;
            parameters = list.ToArray();
        }

        var body = tokens.Skip(index).Where(t => t.Kind != FxTokenKind.Eof).ToList();
        if (body.Count > 0)
            body[0] = body[0] with { Lead = "" };
        macros[name] = new Macro(parameters, body);
    }

    private static List<FxToken> Strip(List<FxToken> tokens) => tokens.Where(t => t.Kind != FxTokenKind.Eof).ToList();

    private List<FxToken> Expand(List<FxToken> tokens, HashSet<string> disabled)
    {
        var result = new List<FxToken>();
        for (var i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];
            if (token.Kind != FxTokenKind.Ident || disabled.Contains(token.Text) || !macros.TryGetValue(token.Text, out var macro))
            {
                result.Add(token);
                continue;
            }

            List<FxToken> replacement;
            if (macro.Parameters == null)
            {
                replacement = macro.Body.ToList();
            }
            else
            {
                if (i + 1 >= tokens.Count || !tokens[i + 1].Is("("))
                {
                    result.Add(token);
                    continue;
                }

                var arguments = new List<List<FxToken>> { new() };
                var depth = 0;
                var j = i + 2;
                for (; j < tokens.Count; j++)
                {
                    var t = tokens[j];
                    if (t.Is("("))
                        depth++;
                    else if (t.Is(")"))
                    {
                        if (depth == 0)
                            break;
                        depth--;
                    }
                    else if (t.Is(",") && depth == 0 && !(macro.Parameters.Length > 0 && macro.Parameters[^1] == "__VA_ARGS__" && arguments.Count == macro.Parameters.Length))
                    {
                        arguments.Add(new List<FxToken>());
                        continue;
                    }

                    arguments[^1].Add(t);
                }

                if (j >= tokens.Count)
                    throw new FxException($"Unterminated macro call: {token.Text}");

                i = j;
                if (macro.Parameters.Length == 0 && arguments.Count == 1 && arguments[0].Count == 0)
                    arguments.Clear();

                replacement = Substitute(macro, arguments, disabled);
            }

            disabled.Add(token.Text);
            var expanded = Expand(replacement, disabled);
            disabled.Remove(token.Text);

            if (expanded.Count > 0)
                expanded[0] = expanded[0] with { Lead = token.Lead };
            result.AddRange(expanded);
        }

        return result;
    }

    private List<FxToken> Substitute(Macro macro, List<List<FxToken>> arguments, HashSet<string> disabled)
    {
        var parameters = macro.Parameters!;
        List<FxToken>? Argument(string name)
        {
            var index = Array.IndexOf(parameters, name);
            if (index < 0)
                return null;
            return index < arguments.Count ? arguments[index] : new List<FxToken>();
        }

        var body = macro.Body;
        var result = new List<FxToken>();
        for (var i = 0; i < body.Count; i++)
        {
            var token = body[i];

            // Stringify: # param
            if (token.Is("#") && i + 1 < body.Count && Argument(body[i + 1].Text) is { } raw)
            {
                result.Add(new FxToken(FxTokenKind.String, "\"" + FxLexer.Join(raw).Trim().Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"", token.Lead));
                i++;
                continue;
            }

            // Paste: left ## right
            if (token.Is("##") && result.Count > 0 && i + 1 < body.Count)
            {
                var next = body[i + 1];
                var right = Argument(next.Text) is { } rightArgument ? rightArgument : new List<FxToken> { next };
                i++;
                if (right.Count == 0)
                    continue;

                var left = result[^1];
                result.RemoveAt(result.Count - 1);
                var pasted = FxLexer.Tokenize(left.Text + right[0].Text.Trim());
                result.AddRange(Strip(pasted).Select((t, index) => index == 0 ? t with { Lead = left.Lead } : t));
                result.AddRange(right.Skip(1));
                continue;
            }

            if (token.Kind == FxTokenKind.Ident && Argument(token.Text) is { } argument)
            {
                // Operands of ## are used unexpanded.
                var adjacentPaste = (i + 1 < body.Count && body[i + 1].Is("##")) || (i > 0 && body[i - 1].Is("##"));
                var tokens = adjacentPaste ? argument : Expand(argument.ToList(), new HashSet<string>(disabled));
                for (var k = 0; k < tokens.Count; k++)
                    result.Add(k == 0 ? tokens[k] with { Lead = token.Lead } : tokens[k]);
                continue;
            }

            result.Add(token);
        }

        return result;
    }

    private bool Evaluate(string expression)
    {
        // Resolve defined(X) / defined X before expansion.
        var tokens = Strip(FxLexer.Tokenize(expression));
        var resolved = new List<FxToken>();
        for (var i = 0; i < tokens.Count; i++)
        {
            if (tokens[i].Is("defined"))
            {
                var parenthesised = i + 1 < tokens.Count && tokens[i + 1].Is("(");
                var nameIndex = i + (parenthesised ? 2 : 1);
                var defined = nameIndex < tokens.Count && macros.ContainsKey(tokens[nameIndex].Text);
                resolved.Add(new FxToken(FxTokenKind.Number, defined ? "1" : "0", " "));
                i = nameIndex + (parenthesised ? 1 : 0);
            }
            else
            {
                resolved.Add(tokens[i]);
            }
        }

        var expanded = Expand(resolved, new HashSet<string>());
        var parser = new ConditionParser(expanded);
        return parser.Parse() != 0;
    }

    private sealed class ConditionParser
    {
        private readonly List<FxToken> tokens;
        private int position;

        public ConditionParser(List<FxToken> tokens) => this.tokens = tokens;

        public long Parse()
        {
            var value = Conditional();
            if (position < tokens.Count)
                throw new FxException($"Unexpected '{tokens[position].Text}' in #if expression");
            return value;
        }

        private FxToken? Peek => position < tokens.Count ? tokens[position] : null;

        private bool Accept(string op)
        {
            if (Peek is { } t && t.Is(op))
            {
                position++;
                return true;
            }

            return false;
        }

        private long Conditional()
        {
            var condition = LogicalOr();
            if (!Accept("?"))
                return condition;

            var whenTrue = Conditional();
            if (!Accept(":"))
                throw new FxException("Expected ':' in #if expression");
            var whenFalse = Conditional();
            return condition != 0 ? whenTrue : whenFalse;
        }

        private long LogicalOr()
        {
            var value = LogicalAnd();
            while (Accept("||"))
            {
                var right = LogicalAnd();
                value = value != 0 || right != 0 ? 1 : 0;
            }

            return value;
        }

        private long LogicalAnd()
        {
            var value = BitOr();
            while (Accept("&&"))
            {
                var right = BitOr();
                value = value != 0 && right != 0 ? 1 : 0;
            }

            return value;
        }

        private long BitOr()
        {
            var value = BitXor();
            while (Peek is { } t && t.Is("|"))
            {
                position++;
                value |= BitXor();
            }

            return value;
        }

        private long BitXor()
        {
            var value = BitAnd();
            while (Accept("^"))
                value ^= BitAnd();
            return value;
        }

        private long BitAnd()
        {
            var value = Equality();
            while (Peek is { } t && t.Is("&"))
            {
                position++;
                value &= Equality();
            }

            return value;
        }

        private long Equality()
        {
            var value = Relational();
            while (true)
            {
                if (Accept("=="))
                    value = value == Relational() ? 1 : 0;
                else if (Accept("!="))
                    value = value != Relational() ? 1 : 0;
                else
                    return value;
            }
        }

        private long Relational()
        {
            var value = Shift();
            while (true)
            {
                if (Accept("<="))
                    value = value <= Shift() ? 1 : 0;
                else if (Accept(">="))
                    value = value >= Shift() ? 1 : 0;
                else if (Accept("<"))
                    value = value < Shift() ? 1 : 0;
                else if (Accept(">"))
                    value = value > Shift() ? 1 : 0;
                else
                    return value;
            }
        }

        private long Shift()
        {
            var value = Additive();
            while (true)
            {
                if (Accept("<<"))
                    value <<= (int)Additive();
                else if (Accept(">>"))
                    value >>= (int)Additive();
                else
                    return value;
            }
        }

        private long Additive()
        {
            var value = Multiplicative();
            while (true)
            {
                if (Accept("+"))
                    value += Multiplicative();
                else if (Accept("-"))
                    value -= Multiplicative();
                else
                    return value;
            }
        }

        private long Multiplicative()
        {
            var value = Unary();
            while (true)
            {
                if (Accept("*"))
                    value *= Unary();
                else if (Accept("/"))
                {
                    var divisor = Unary();
                    value = divisor == 0 ? 0 : value / divisor;
                }
                else if (Accept("%"))
                {
                    var divisor = Unary();
                    value = divisor == 0 ? 0 : value % divisor;
                }
                else
                    return value;
            }
        }

        private long Unary()
        {
            if (Accept("!"))
                return Unary() == 0 ? 1 : 0;
            if (Accept("~"))
                return ~Unary();
            if (Accept("-"))
                return -Unary();
            if (Accept("+"))
                return Unary();
            return Primary();
        }

        private long Primary()
        {
            if (Accept("("))
            {
                var value = Conditional();
                if (!Accept(")"))
                    throw new FxException("Expected ')' in #if expression");
                return value;
            }

            if (Peek is not { } token)
                throw new FxException("Unexpected end of #if expression");
            position++;

            if (token.Kind == FxTokenKind.Number)
            {
                var text = token.Text.TrimEnd('u', 'U', 'l', 'L', 'f', 'F');
                if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                    return long.Parse(text.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                if (text.Contains('.') || text.Contains('e') || text.Contains('E'))
                    return (long)double.Parse(text, CultureInfo.InvariantCulture);
                if (text.Length > 1 && text[0] == '0')
                    return Convert.ToInt64(text, 8);
                return long.Parse(text, CultureInfo.InvariantCulture);
            }

            // true/false and any identifier that survived macro expansion
            return token.Text == "true" ? 1 : 0;
        }
    }

    /// <summary>Comments become whitespace; newlines inside them survive so lines stay countable.</summary>
    private static string StripComments(string text)
    {
        var sb = new StringBuilder(text.Length);
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (c is '"' or '\'')
            {
                sb.Append(c);
                i++;
                while (i < text.Length && text[i] != c && text[i] != '\n')
                {
                    if (text[i] == '\\' && i + 1 < text.Length)
                        sb.Append(text[i++]);
                    sb.Append(text[i++]);
                }

                if (i < text.Length && text[i] == c)
                    sb.Append(text[i++]);
            }
            else if (c == '/' && i + 1 < text.Length && text[i + 1] == '/')
            {
                while (i < text.Length && text[i] != '\n')
                    i++;
            }
            else if (c == '/' && i + 1 < text.Length && text[i + 1] == '*')
            {
                i += 2;
                while (i + 1 < text.Length && !(text[i] == '*' && text[i + 1] == '/'))
                {
                    if (text[i] == '\n')
                        sb.Append('\n');
                    i++;
                }

                i += 2;
                sb.Append(' ');
            }
            else
            {
                sb.Append(c);
                i++;
            }
        }

        return sb.ToString();
    }

    /// <summary>Splits into lines, joining those that end with a backslash.</summary>
    private static List<string> SplitLines(string text)
    {
        var lines = new List<string>();
        var current = new StringBuilder();
        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            if (raw.EndsWith('\\'))
            {
                current.Append(raw, 0, raw.Length - 1).Append(' ');
                continue;
            }

            current.Append(raw);
            lines.Add(current.ToString());
            current.Clear();
        }

        if (current.Length > 0)
            lines.Add(current.ToString());
        return lines;
    }
}
