using System.Globalization;
using System.Text;
using System.Text.Json;

namespace CodexBar.Core.Internal;

/// <summary>
/// Tolerant JSON helpers. Provider payloads are additive and keep moving, so every read is
/// optional and shaped like the upstream Swift decoders: a missing key yields null and never
/// a fabricated zero.
/// </summary>
internal static class JsonUtil
{
    public static JsonElement? ObjectOrNull(JsonElement parent, params string[] names)
    {
        foreach (var name in names)
        {
            if (parent.ValueKind == JsonValueKind.Object &&
                parent.TryGetProperty(name, out var value) &&
                value.ValueKind == JsonValueKind.Object)
            {
                return value;
            }
        }

        return null;
    }

    public static string? NonEmptyString(JsonElement parent, params string[] names)
    {
        foreach (var name in names)
        {
            if (parent.ValueKind != JsonValueKind.Object || !parent.TryGetProperty(name, out var value))
            {
                continue;
            }

            if (value.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var text = value.GetString()?.Trim();
            if (!string.IsNullOrEmpty(text))
            {
                return text;
            }
        }

        return null;
    }

    /// <summary>
    /// Reads a finite number. Non-finite values (NaN/Infinity) and non-numeric payloads are
    /// reported as absent so callers skip the window instead of reporting a bogus 0%.
    /// </summary>
    public static double? FiniteNumber(JsonElement parent, params string[] names)
    {
        foreach (var name in names)
        {
            if (parent.ValueKind != JsonValueKind.Object || !parent.TryGetProperty(name, out var value))
            {
                continue;
            }

            if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number))
            {
                return double.IsFinite(number) ? number : null;
            }

            if (value.ValueKind == JsonValueKind.String &&
                double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
            {
                return double.IsFinite(parsed) ? parsed : null;
            }

            return null;
        }

        return null;
    }

    public static long? Integer(JsonElement parent, params string[] names)
    {
        foreach (var name in names)
        {
            if (parent.ValueKind != JsonValueKind.Object || !parent.TryGetProperty(name, out var value))
            {
                continue;
            }

            if (value.ValueKind != JsonValueKind.Number)
            {
                return null;
            }

            if (value.TryGetInt64(out var integral))
            {
                return integral;
            }

            if (value.TryGetDouble(out var fractional) && double.IsFinite(fractional))
            {
                return (long)Math.Truncate(fractional);
            }

            return null;
        }

        return null;
    }

    public static bool? Boolean(JsonElement parent, params string[] names)
    {
        foreach (var name in names)
        {
            if (parent.ValueKind != JsonValueKind.Object || !parent.TryGetProperty(name, out var value))
            {
                continue;
            }

            return value.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => null,
            };
        }

        return null;
    }

    /// <summary>
    /// Validates a reported utilization. Returns null for non-finite input (caller skips the
    /// window) and clamps finite out-of-range values into the 0..100 contract.
    /// </summary>
    public static double? ClampPercent(double? value)
    {
        if (value is not { } number || !double.IsFinite(number))
        {
            return null;
        }

        return Math.Clamp(number, 0, 100);
    }

    public static DateTimeOffset? FromUnixSeconds(long? seconds)
    {
        if (seconds is not { } value || value <= 0)
        {
            return null;
        }

        // Guard against values a malformed payload could push outside DateTimeOffset's range.
        if (value < -62_135_596_800 || value > 253_402_300_799)
        {
            return null;
        }

        return DateTimeOffset.FromUnixTimeSeconds(value);
    }

    public static DateTimeOffset? ParseIso8601(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
            out var parsed)
            ? parsed.ToUniversalTime()
            : null;
    }

    /// <summary>Stable ASCII slug used to derive window ids, mirroring the upstream mapper.</summary>
    public static string Slug(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(value.Length);
        var lastWasDash = false;

        foreach (var ch in value.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch))
            {
                builder.Append(ch);
                lastWasDash = false;
            }
            else if (!lastWasDash && builder.Length > 0)
            {
                builder.Append('-');
                lastWasDash = true;
            }
        }

        return builder.ToString().Trim('-');
    }

    public static string? FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return null;
    }
}
