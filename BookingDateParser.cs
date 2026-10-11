using System.Globalization;
using System.Text.RegularExpressions;

namespace JevTest;

internal static partial class BookingDateParser
{
    private static readonly string[] MonthNames = CultureInfo.GetCultureInfo("da-DK").DateTimeFormat.MonthNames;

    public static string Normalize(string message, DateOnly today, BookingField? answering)
    {
        return DatePattern().Replace(message, match =>
        {
            var hasYear = match.Groups["year"].Success;
            var hasMonthName = match.Groups["monthName"].Success;
            if (!hasYear && !hasMonthName &&
                ((answering is BookingField.Adults or BookingField.Children && message.Trim() == match.Value) ||
                 CountBeforePattern().IsMatch(message[..match.Index]) ||
                 CountAfterPattern().IsMatch(message[(match.Index + match.Length)..])))
            {
                return match.Value;
            }

            var day = int.Parse(match.Groups["day"].Value, CultureInfo.InvariantCulture);
            var month = hasMonthName
                ? Array.FindIndex(MonthNames, name =>
                    name.Equals(match.Groups["monthName"].Value, StringComparison.OrdinalIgnoreCase)) + 1
                : int.Parse(match.Groups["month"].Value, CultureInfo.InvariantCulture);
            var year = hasYear
                ? int.Parse(match.Groups["year"].Value, CultureInfo.InvariantCulture)
                : NextYear(day, month, today);

            // Keep impossible dates as invalid ISO candidates so normal booking validation asks for a correction.
            return FormattableString.Invariant($"{year:D4}-{month:D2}-{day:D2}");
        });
    }

    private static int NextYear(int day, int month, DateOnly today)
    {
        if (month is < 1 or > 12 || day < 1 || day > DateTime.DaysInMonth(2000, month))
            return today.Year;

        // Eight years covers the leap-day gap around a century that is not a leap year.
        for (var year = today.Year; year <= Math.Min(today.Year + 8, 9999); year++)
        {
            if (day <= DateTime.DaysInMonth(year, month) && new DateOnly(year, month, day) >= today)
                return year;
        }

        throw new ArgumentException("Datoens næste forekomst ligger uden for det understøttede datointerval.");
    }

    [GeneratedRegex("""
        (?<![\p{L}\p{N}./-])
        (?:
            (?<year>[0-9]{4})-(?<month>[0-9]{1,2})-(?<day>[0-9]{1,2})
          | (?<day>[0-9]{1,2})(?<separator>[./-])(?<month>[0-9]{1,2})(?:\k<separator>(?<year>[0-9]{4}))?
          | (?<day>[0-9]{1,2})\.?\s+
            (?<monthName>januar|februar|marts|april|maj|juni|juli|august|september|oktober|november|december)
            (?:\s+(?<year>[0-9]{4}))?
        )
        (?![\p{L}\p{N}]|[./-][0-9])
        """, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.IgnorePatternWhitespace)]
    private static partial Regex DatePattern();

    [GeneratedRegex(@"(?:voksne|voksen|børn|barn|personer|person)\s*[:=]?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CountBeforePattern();

    [GeneratedRegex(@"^\s*(?:voksne|voksen|børn|barn|personer|person)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CountAfterPattern();
}
