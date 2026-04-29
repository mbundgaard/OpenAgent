using System.Globalization;

namespace OpenAgent.App.Converters;

/// <summary>
/// Formats a <see cref="DateTimeOffset"/> as a short relative-time label
/// ("just now", "5m ago", "3h ago", "2d ago", or a localized "d MMM" date once
/// it falls outside the past week). Used by the conversations list to render
/// the row timestamp column.
/// </summary>
public sealed class RelativeTimeConverter : IValueConverter
{
    /// <summary>Converts a <see cref="DateTimeOffset"/> to a relative-time label. Non-DateTimeOffset values render as an em-dash.</summary>
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not DateTimeOffset dt) return "—";
        var d = DateTimeOffset.UtcNow - dt;
        if (d.TotalMinutes < 1) return "just now";
        if (d.TotalMinutes < 60) return $"{(int)d.TotalMinutes}m ago";
        if (d.TotalHours < 24) return $"{(int)d.TotalHours}h ago";
        if (d.TotalDays < 7) return $"{(int)d.TotalDays}d ago";
        return dt.LocalDateTime.ToString("d MMM");
    }

    /// <summary>Not supported — this is a one-way display converter.</summary>
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>
/// Maps a boolean "muted" state to a button label: <c>true</c> -> "Unmute", <c>false</c> -> "Mute".
/// Used by the call page mute toggle (Task 17).
/// </summary>
public sealed class MuteLabelConverter : IValueConverter
{
    /// <summary>Converts a boolean muted state to the corresponding action label.</summary>
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => (value is bool muted && muted) ? "Unmute" : "Mute";

    /// <summary>Not supported — this is a one-way display converter.</summary>
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
