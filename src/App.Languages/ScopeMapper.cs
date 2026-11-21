using App.Core.Models;

namespace App.Languages;

/// <summary>Traduit les portées TextMate d'un jeton en TokenKind.</summary>
public static class ScopeMapper
{
    public static TokenKind Map(IEnumerable<string> scopes)
    {
        // La portée la plus précise est la dernière de la liste.
        foreach (var scope in scopes.Reverse())
        {
            var kind = MapOne(scope);
            if (kind != TokenKind.Default) return kind;
        }
        return TokenKind.Default;
    }

    private static TokenKind MapOne(string s)
    {
        // L'ordre compte : les cas précis passent avant les cas généraux.
        if (s.StartsWith("invalid")) return TokenKind.Error;

        if (s.StartsWith("comment")) return TokenKind.Comment;

        if (s.StartsWith("constant.character.escape")) return TokenKind.StringEscape;
        if (s.StartsWith("string")) return TokenKind.String;

        if (s.StartsWith("constant.numeric")) return TokenKind.Number;
        if (s.StartsWith("constant")) return TokenKind.Constant;

        if (s.StartsWith("meta.preprocessor") || s.StartsWith("keyword.control.directive")
            || s.StartsWith("keyword.other.preprocessor")) return TokenKind.Preprocessor;

        if (s.StartsWith("keyword.operator")) return TokenKind.Operator;
        if (s.StartsWith("keyword") || s.StartsWith("storage.modifier")) return TokenKind.Keyword;
        if (s.StartsWith("storage.type") && s.Contains("function")) return TokenKind.Keyword;
        if (s.StartsWith("storage")) return TokenKind.Keyword;

        if (s.StartsWith("entity.name.function") || s.StartsWith("support.function")
            || s.StartsWith("meta.function-call")) return TokenKind.Function;

        if (s.StartsWith("entity.name.namespace") || s.StartsWith("entity.name.module")
            || s.StartsWith("support.module")) return TokenKind.Namespace;

        if (s.StartsWith("entity.name.type") || s.StartsWith("entity.name.class")
            || s.StartsWith("support.type") || s.StartsWith("support.class")) return TokenKind.Type;

        if (s.StartsWith("entity.name.tag")) return TokenKind.Tag;
        if (s.StartsWith("entity.other.attribute-name")) return TokenKind.Attribute;

        if (s.StartsWith("variable.parameter")) return TokenKind.Parameter;
        if (s.StartsWith("variable.other.property") || s.StartsWith("variable.other.object.property")
            || s.StartsWith("support.variable.property") || s.StartsWith("meta.property")) return TokenKind.Property;
        if (s.StartsWith("variable")) return TokenKind.Variable;

        if (s.StartsWith("markup.heading") || s.StartsWith("entity.name.section")) return TokenKind.Heading;
        if (s.StartsWith("markup.bold")) return TokenKind.Bold;
        if (s.StartsWith("markup.italic")) return TokenKind.Italic;
        if (s.StartsWith("markup.underline.link") || s.StartsWith("string.other.link")) return TokenKind.Link;
        if (s.StartsWith("markup.inline.raw") || s.StartsWith("markup.fenced_code")
            || s.StartsWith("markup.raw")) return TokenKind.Code;

        if (s.StartsWith("punctuation")) return TokenKind.Punctuation;

        return TokenKind.Default;
    }
}