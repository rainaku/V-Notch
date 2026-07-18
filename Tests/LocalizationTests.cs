using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

public sealed class LocalizationTests
{
    private static readonly string[] Languages = { "en", "vi", "es", "fr", "de", "ja", "hi" };

    [Fact]
    public void AllSupportedLanguagesHaveTheSameTranslationKeys()
    {
        var englishKeys = Loc.GetKeys("en").OrderBy(key => key, StringComparer.Ordinal).ToArray();

        Assert.NotEmpty(englishKeys);
        foreach (var language in Languages)
        {
            var keys = Loc.GetKeys(language).OrderBy(key => key, StringComparer.Ordinal).ToArray();
            Assert.Equal(englishKeys, keys);
        }
    }

    [Fact]
    public void FormatPlaceholdersMatchAcrossLanguages()
    {
        foreach (var key in Loc.GetKeys("en"))
        {
            Loc.SetLanguage("en");
            var expected = GetPlaceholderIndexes(Loc.Get(key));

            foreach (var language in Languages.Skip(1))
            {
                Loc.SetLanguage(language);
                Assert.Equal(expected, GetPlaceholderIndexes(Loc.Get(key)));
            }
        }

        Loc.SetLanguage("en");
    }

    [Fact]
    public void HindiTranslationsDoNotFallBackToEnglish()
    {
        var allowedTechnicalLabels = new HashSet<string>(StringComparer.Ordinal)
        {
            "settings.skin.liquidglass",
            "settings.youtubeApiKey"
        };
        var untranslated = new List<string>();

        foreach (var key in Loc.GetKeys("en"))
        {
            Loc.SetLanguage("en");
            string english = Loc.Get(key);
            Loc.SetLanguage("hi");
            string hindi = Loc.Get(key);

            if (!string.IsNullOrWhiteSpace(english) &&
                string.Equals(english, hindi, StringComparison.Ordinal) &&
                !allowedTechnicalLabels.Contains(key))
            {
                untranslated.Add(key);
            }
        }

        Loc.SetLanguage("en");
        Assert.Empty(untranslated);
    }

    [Fact]
    public void HindiTranslationsContainNativeScriptOutsideTechnicalLabels()
    {
        var allowedTechnicalLabels = new HashSet<string>(StringComparer.Ordinal)
        {
            "settings.skin.liquidglass",
            "settings.youtubeApiKey"
        };
        var nonNativeValues = new List<string>();

        Loc.SetLanguage("hi");
        foreach (var key in Loc.GetKeys("hi"))
        {
            string value = Loc.Get(key);
            if (!string.IsNullOrWhiteSpace(value) &&
                !allowedTechnicalLabels.Contains(key) &&
                !Regex.IsMatch(value, "[\\u0900-\\u097F]"))
            {
                nonNativeValues.Add(key);
            }
        }

        Loc.SetLanguage("en");
        Assert.Empty(nonNativeValues);
    }

    [Fact]
    public void HindiAppearsInTheLanguagePickerWithItsNativeName()
    {
        Assert.Contains(Loc.GetAvailableLanguages(), language =>
            language.Code == "hi" && language.Name == "हिन्दी");
    }

    [Fact]
    public void EveryLiteralLocalizationKeyUsedByTheAppExists()
    {
        string repositoryRoot = FindRepositoryRoot();
        var usedKeys = new HashSet<string>(StringComparer.Ordinal);
        var keyPattern = new Regex("(?:Loc\\.Get\\(|LocalizationKey\\s*=\\s*)\\\"(?<key>[^\\\"]+)\\\"",
            RegexOptions.Compiled);

        foreach (var file in Directory.EnumerateFiles(repositoryRoot, "*.*", SearchOption.AllDirectories)
                     .Where(path => path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) ||
                                    path.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase))
                     .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") &&
                                    !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") &&
                                    !path.Contains($"{Path.DirectorySeparatorChar}artifacts{Path.DirectorySeparatorChar}")))
        {
            foreach (Match match in keyPattern.Matches(File.ReadAllText(file)))
                usedKeys.Add(match.Groups["key"].Value);
        }

        var englishKeys = Loc.GetKeys("en").ToHashSet(StringComparer.Ordinal);
        Assert.Empty(usedKeys.Except(englishKeys, StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("en", "en-US")]
    [InlineData("vi", "vi-VN")]
    [InlineData("es", "es-ES")]
    [InlineData("fr", "fr-FR")]
    [InlineData("de", "de-DE")]
    [InlineData("ja", "ja-JP")]
    [InlineData("hi", "hi-IN")]
    public void LanguageUsesTheExpectedCulture(string language, string culture)
    {
        Loc.SetLanguage(language);
        Assert.Equal(culture, Loc.GetCulture().Name);
        Loc.SetLanguage("en");
    }

    private static int[] GetPlaceholderIndexes(string value) =>
        Regex.Matches(value, @"\{(?<index>\d+)(?:[^}]*)\}")
            .Select(match => int.Parse(match.Groups["index"].Value))
            .Distinct()
            .OrderBy(index => index)
            .ToArray();

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "V-Notch.csproj")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the V-Notch repository root.");
    }
}
