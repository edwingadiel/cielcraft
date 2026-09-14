namespace CielCraft.Core;

/// <summary>
/// Extracts the first integer from UI text bytes (UTF-8) without allocating.
/// Digit groups joined by a thousands separator — comma, period, space,
/// no-break space or narrow no-break space followed by exactly three digits
/// — are merged ("12,345", "12.345", "12 345"), so the client's number
/// formatting cannot truncate a value. Anything else ends the number
/// ("12345 / 15000" is 12345). Overflow saturates.
/// </summary>
public static class DigitParser
{
    public static int ParseFirstInt(ReadOnlySpan<byte> text)
    {
        var i = 0;
        while (i < text.Length && !IsDigit(text[i]))
            i++;

        if (i == text.Length)
            return 0;

        long value = 0;
        while (i < text.Length)
        {
            if (IsDigit(text[i]))
            {
                value = Math.Min(int.MaxValue, value * 10 + (text[i] - (byte)'0'));
                i++;
                continue;
            }

            var separator = SeparatorLength(text[i..]);
            if (separator > 0 && IsThreeDigitGroup(text[(i + separator)..]))
            {
                i += separator;
                continue;
            }

            break;
        }

        return (int)value;
    }

    private static bool IsDigit(byte b) => b is >= (byte)'0' and <= (byte)'9';

    private static int SeparatorLength(ReadOnlySpan<byte> text)
    {
        if (text.Length == 0)
            return 0;

        if (text[0] is (byte)',' or (byte)'.' or (byte)' ')
            return 1;

        // U+00A0 no-break space (C2 A0), U+202F narrow no-break space (E2 80 AF).
        if (text.Length >= 2 && text[0] == 0xC2 && text[1] == 0xA0)
            return 2;
        if (text.Length >= 3 && text[0] == 0xE2 && text[1] == 0x80 && text[2] == 0xAF)
            return 3;

        return 0;
    }

    private static bool IsThreeDigitGroup(ReadOnlySpan<byte> text) =>
        text.Length >= 3
        && IsDigit(text[0]) && IsDigit(text[1]) && IsDigit(text[2])
        && (text.Length == 3 || !IsDigit(text[3]));
}
