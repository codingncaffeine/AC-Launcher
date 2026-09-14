using System.Text;

namespace ACLauncher.Core.Steam;

/// <summary>
/// Parser for Valve's text KeyValues format (<c>libraryfolders.vdf</c>, <c>config.vdf</c>,
/// <c>compatibilitytool.vdf</c>, <c>toolmanifest.vdf</c>, <c>appmanifest_*.acf</c>).
/// </summary>
public static class KeyValuesText
{
    /// <summary>Parses a document into a synthetic root section whose children are the top-level keys.</summary>
    /// <exception cref="FormatException">The text is not well-formed KeyValues.</exception>
    public static KvNode Parse(string text)
    {
        var reader = new Reader(text);
        var root = KvNode.Section("");
        ParseBody(reader, root, topLevel: true);
        return root;
    }

    public static KvNode? TryParseFile(string path)
    {
        try
        {
            return File.Exists(path) ? Parse(File.ReadAllText(path)) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or FormatException)
        {
            return null;
        }
    }

    private static void ParseBody(Reader reader, KvNode section, bool topLevel)
    {
        while (true)
        {
            var token = reader.Next();
            if (token is null)
            {
                if (topLevel) return;
                throw new FormatException("Unexpected end of KeyValues text: missing '}'.");
            }
            if (token.Value.Kind == TokenKind.Close)
            {
                if (topLevel) throw new FormatException($"Unbalanced '}}' at offset {token.Value.Offset}.");
                return;
            }
            if (token.Value.Kind != TokenKind.String)
                throw new FormatException($"Expected a key at offset {token.Value.Offset}.");

            var key = token.Value.Text;
            var value = reader.Next() ?? throw new FormatException($"Key '{key}' has no value.");
            // A platform conditional such as [$WIN32] may follow either the key or the value.
            if (value.Kind == TokenKind.Conditional)
                value = reader.Next() ?? throw new FormatException($"Key '{key}' has no value.");

            switch (value.Kind)
            {
                case TokenKind.Open:
                    var child = KvNode.Section(key);
                    ParseBody(reader, child, topLevel: false);
                    section.Add(child);
                    break;
                case TokenKind.String:
                    section.Add(KvNode.Leaf(key, value.Text));
                    reader.SkipConditional();
                    break;
                default:
                    throw new FormatException($"Key '{key}' has an invalid value at offset {value.Offset}.");
            }
        }
    }

    private enum TokenKind { String, Open, Close, Conditional }

    private readonly record struct Token(TokenKind Kind, string Text, int Offset);

    private sealed class Reader(string text)
    {
        private int _pos;

        public void SkipConditional()
        {
            var save = _pos;
            var token = Next();
            if (token is not { Kind: TokenKind.Conditional }) _pos = save;
        }

        public Token? Next()
        {
            SkipTrivia();
            if (_pos >= text.Length) return null;
            var start = _pos;
            var c = text[_pos];
            switch (c)
            {
                case '{': _pos++; return new Token(TokenKind.Open, "{", start);
                case '}': _pos++; return new Token(TokenKind.Close, "}", start);
                case '"': return new Token(TokenKind.String, ReadQuoted(), start);
                case '[':
                    var end = text.IndexOf(']', _pos);
                    if (end < 0) throw new FormatException($"Unterminated conditional at offset {start}.");
                    _pos = end + 1;
                    return new Token(TokenKind.Conditional, text[start.._pos], start);
                default:
                    while (_pos < text.Length && !char.IsWhiteSpace(text[_pos]) && text[_pos] is not ('{' or '}' or '"'))
                        _pos++;
                    return new Token(TokenKind.String, text[start.._pos], start);
            }
        }

        private void SkipTrivia()
        {
            while (_pos < text.Length)
            {
                if (char.IsWhiteSpace(text[_pos]))
                {
                    _pos++;
                }
                else if (text[_pos] == '/' && _pos + 1 < text.Length && text[_pos + 1] == '/')
                {
                    var newline = text.IndexOf('\n', _pos);
                    _pos = newline < 0 ? text.Length : newline + 1;
                }
                else
                {
                    return;
                }
            }
        }

        private string ReadQuoted()
        {
            var start = _pos;
            _pos++; // opening quote
            var sb = new StringBuilder();
            while (_pos < text.Length)
            {
                var c = text[_pos++];
                if (c == '"') return sb.ToString();
                if (c == '\\' && _pos < text.Length)
                {
                    var escaped = text[_pos++];
                    sb.Append(escaped switch
                    {
                        'n' => '\n',
                        't' => '\t',
                        _ => escaped, // \\ and \" and anything else literal
                    });
                    continue;
                }
                sb.Append(c);
            }
            throw new FormatException($"Unterminated string at offset {start}.");
        }
    }
}
