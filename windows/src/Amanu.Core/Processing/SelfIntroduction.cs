using System.Text.RegularExpressions;

namespace Amanu.Core.Processing;

public static class SelfIntroduction
{
    // Conservative: an explicit introduction at the start of a speaker's own utterance.
    // A name addressed by another speaker is never evidence for that speaker's identity.
    private static readonly Regex Pattern = new(
        @"\A\s*(?:(?:Здравствуйте|Привет|Добрый день|Hello|Hi)[,.!]?\s+)?(?:Меня зовут|Я\s*[—–-]?|My name is)\s+(?<name>[\p{Lu}][\p{Ll}]+(?:[-'][\p{Lu}][\p{Ll}]+)?(?:\s+[\p{Lu}][\p{Ll}]+){0,2})(?=\s*(?:[,.;!?:—–-]|$))",
        RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    public static string? Name(string utterance)
    {
        // Case-insensitivity for the introduction, without accepting lowercase words as names.
        var text = Regex.Replace(utterance, @"\A\s*(меня зовут|я|my name is)\b",
            match => match.Value.Trim() switch { "я" => "Я", "my name is" => "My name is", _ => "Меня зовут" });
        var match = Pattern.Match(text);
        return match.Success ? match.Groups["name"].Value : null;
    }

    public static string? UniqueName(IEnumerable<string> utterances)
    {
        var names = utterances.Select(Name).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).Take(2).ToArray();
        return names.Length == 1 ? names[0] : null;
    }

    public static bool Supports(string name, string quote, IEnumerable<string> utterances) =>
        string.Equals(Name(quote), name.Trim(), StringComparison.OrdinalIgnoreCase)
        && string.Equals(UniqueName(utterances), name.Trim(), StringComparison.OrdinalIgnoreCase)
        && utterances.Any(text => string.Equals(Name(text), name.Trim(), StringComparison.OrdinalIgnoreCase)
            && text.Contains(quote, StringComparison.OrdinalIgnoreCase));
}
