using System.Globalization;
using System.Text;

namespace PersonalLauncher.Core.Services;

public static class TextNormalization
{
    /// <summary>
    /// Builds a normalized search key:
    /// - Trims and collapses whitespace
    /// - Strips Vietnamese/Latin diacritics (including đ/Đ -> d)
    /// - Lowercases Latin text
    /// - Preserves Japanese/Chinese characters intact without transliteration
    /// </summary>
    public static string NormalizeForSearch(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return string.Empty;

        string trimmed = input.Trim();
        var sb = new StringBuilder(trimmed.Length);
        bool previousWasSpace = false;

        foreach (char ch in trimmed)
        {
            if (char.IsWhiteSpace(ch))
            {
                if (!previousWasSpace)
                {
                    sb.Append(' ');
                    previousWasSpace = true;
                }
                continue;
            }

            previousWasSpace = false;

            if (ch == 'đ' || ch == 'Đ')
            {
                sb.Append('d');
                continue;
            }

            // Preserve CJK / Kana characters as-is without stripping dakuten/handakuten!
            if (IsCjkOrKana(ch))
            {
                sb.Append(ch);
                continue;
            }

            // Decompose Latin/Vietnamese characters and drop combining diacritic marks
            string decomposed = ch.ToString().Normalize(NormalizationForm.FormD);
            foreach (char d in decomposed)
            {
                UnicodeCategory cat = CharUnicodeInfo.GetUnicodeCategory(d);
                if (cat != UnicodeCategory.NonSpacingMark &&
                    cat != UnicodeCategory.SpacingCombiningMark &&
                    cat != UnicodeCategory.EnclosingMark)
                {
                    sb.Append(char.ToLowerInvariant(d));
                }
            }
        }

        return sb.ToString().Trim();
    }

    public static bool IsCjkOrKana(char ch)
    {
        return (ch >= '\u3040' && ch <= '\u309F') || // Hiragana
               (ch >= '\u30A0' && ch <= '\u30FF') || // Katakana
               (ch >= '\u31F0' && ch <= '\u31FF') || // Katakana Phonetic Extensions
               (ch >= '\u3400' && ch <= '\u4DBF') || // CJK Unified Ideographs Extension A
               (ch >= '\u4E00' && ch <= '\u9FFF') || // CJK Unified Ideographs
               (ch >= '\uF900' && ch <= '\uFAFF') || // CJK Compatibility Ideographs
               (ch >= '\uFF66' && ch <= '\uFF9F');   // Halfwidth Katakana
    }

    public static string NormalizeDisplayName(string? name, int maxLength = 120)
    {
        if (string.IsNullOrWhiteSpace(name))
            return string.Empty;

        string trimmed = name.Trim();
        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength].TrimEnd();
    }
}
