using System.Globalization;
using System.Text;

namespace VNotch.Services;

internal static class SettingsSearchMatcher
{
    private const double FuzzyMatchThreshold = 0.75;

    public static string Normalize(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return string.Empty;

        string decomposed = input.Normalize(NormalizationForm.FormKD);
        var result = new StringBuilder(decomposed.Length);
        bool previousWasSeparator = true;

        foreach (char character in decomposed)
        {
            UnicodeCategory category = CharUnicodeInfo.GetUnicodeCategory(character);
            if (category is UnicodeCategory.NonSpacingMark
                or UnicodeCategory.SpacingCombiningMark
                or UnicodeCategory.EnclosingMark)
            {
                continue;
            }

            char normalizedCharacter = character switch
            {
                'đ' or 'Đ' => 'd',
                _ => char.ToLowerInvariant(character)
            };

            if (char.IsLetterOrDigit(normalizedCharacter))
            {
                result.Append(normalizedCharacter);
                previousWasSeparator = false;
            }
            else if (!previousWasSeparator)
            {
                result.Append(' ');
                previousWasSeparator = true;
            }
        }

        if (result.Length > 0 && result[^1] == ' ')
        {
            result.Length--;
        }

        return result.ToString();
    }

    public static bool IsMatch(string sourceText, string query) =>
        IsNormalizedMatch(Normalize(sourceText), Normalize(query));

    public static bool IsNormalizedMatch(string normalizedSource, string normalizedQuery)
    {
        if (normalizedSource.Length == 0 || normalizedQuery.Length == 0)
        {
            return false;
        }

        if (normalizedSource.Contains(normalizedQuery, StringComparison.Ordinal))
        {
            return true;
        }

        string[] sourceWords = normalizedSource.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string[] queryWords = normalizedQuery.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        // A multi-word query may describe different parts of one setting (for example
        // "album blur"), so every query word may match independently and in any order.
        return queryWords.Length > 0 && queryWords.All(queryWord =>
            SourceContainsWord(normalizedSource, sourceWords, queryWord));
    }

    private static bool SourceContainsWord(string source, string[] sourceWords, string queryWord)
    {
        if (source.Contains(queryWord, StringComparison.Ordinal))
        {
            return true;
        }

        if (queryWord.Length < 3)
        {
            return false;
        }

        foreach (string sourceWord in sourceWords)
        {
            if (sourceWord.Length < 3) continue;

            if (CalculateSimilarity(queryWord, sourceWord) > FuzzyMatchThreshold)
            {
                return true;
            }
        }

        return false;
    }

    private static double CalculateSimilarity(string source, string target)
    {
        int distance = CalculateLevenshteinDistance(source, target);
        return 1.0 - (double)distance / Math.Max(source.Length, target.Length);
    }

    private static int CalculateLevenshteinDistance(string source, string target)
    {
        if (source.Length == 0) return target.Length;
        if (target.Length == 0) return source.Length;

        int[] previousRow = new int[target.Length + 1];
        int[] currentRow = new int[target.Length + 1];

        for (int column = 0; column < previousRow.Length; column++)
        {
            previousRow[column] = column;
        }

        for (int row = 0; row < source.Length; row++)
        {
            currentRow[0] = row + 1;
            for (int column = 0; column < target.Length; column++)
            {
                int substitutionCost = source[row] == target[column] ? 0 : 1;
                currentRow[column + 1] = Math.Min(
                    Math.Min(currentRow[column] + 1, previousRow[column + 1] + 1),
                    previousRow[column] + substitutionCost);
            }

            (previousRow, currentRow) = (currentRow, previousRow);
        }

        return previousRow[target.Length];
    }
}
