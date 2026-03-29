using System.Globalization;
using System.Text;

namespace VNotch.Services;

public sealed class YouTubeLookupResult
{
    public string? Id { get; init; }
    public string? Author { get; init; }
    public string? Title { get; init; }
    public TimeSpan Duration { get; init; }

    public bool TitleMatches(string otherTitle)
    {
        if (string.IsNullOrEmpty(Title) || string.IsNullOrEmpty(otherTitle))
        {
            return false;
        }

        string t1 = NormalizeForLooseMatch(Title);
        string t2 = NormalizeForLooseMatch(otherTitle);
        return t1.Contains(t2, StringComparison.Ordinal) || t2.Contains(t1, StringComparison.Ordinal);
    }

    private static string NormalizeForLooseMatch(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        string folded = value.Normalize(NormalizationForm.FormD);
        var sb = new System.Text.StringBuilder(folded.Length);
        bool lastWasSpace = false;

        foreach (var ch in folded)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            if (char.IsLetterOrDigit(ch))
            {
                sb.Append(char.ToLowerInvariant(ch));
                lastWasSpace = false;
            }
            else if (!lastWasSpace)
            {
                sb.Append(' ');
                lastWasSpace = true;
            }
        }

        if (sb.Length > 0 && sb[^1] == ' ')
        {
            sb.Length--;
        }

        return sb.ToString();
    }
}
