using System.Text.RegularExpressions;

namespace AryanEbookLibrary.Services.Metadata;

/// <summary>ISBN-10/13 found in text and checked by their check digit, so page numbers and phone numbers do not pass.</summary>
public static class Isbn
{
    private static readonly Regex Candidate = new(
        @"(?:ISBN(?:-1[03])?[:\s]*)([0-9Xx][0-9Xx\s-]{8,16}[0-9Xx])|(97[89][\d\s-]{10,14}\d)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>The first valid ISBN in <paramref name="text"/>, as bare digits (13 or 10), or null.</summary>
    public static string? Find(string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        foreach (Match m in Candidate.Matches(text))
        {
            var digits = Digits(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value);
            if (IsValid(digits)) return digits;
        }
        return null;
    }

    /// <summary>Digits (and a final X) only: "978-0-14-310324-0" → "9780143103240".</summary>
    public static string Digits(string s) =>
        new string(s.Where(c => char.IsAsciiDigit(c) || c is 'X' or 'x').ToArray()).ToUpperInvariant();

    public static bool IsValid(string? d)
    {
        if (d is null) return false;
        if (d.Length == 10)
        {
            var sum = 0;
            for (var i = 0; i < 10; i++)
            {
                var v = d[i] == 'X' ? 10 : d[i] - '0';
                if (v is < 0 or > 10 || v == 10 && i != 9) return false;
                sum += v * (10 - i);
            }
            return sum % 11 == 0;
        }
        if (d.Length == 13 && d.All(char.IsAsciiDigit) && (d.StartsWith("978") || d.StartsWith("979")))
        {
            var sum = 0;
            for (var i = 0; i < 13; i++) sum += (d[i] - '0') * (i % 2 == 0 ? 1 : 3);
            return sum % 10 == 0;
        }
        return false;
    }
}
