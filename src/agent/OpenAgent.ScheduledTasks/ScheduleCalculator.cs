using Cronos;
using OpenAgent.ScheduledTasks.Models;

namespace OpenAgent.ScheduledTasks;

/// <summary>
/// Pure functions for schedule math — computing when a task should run next, and validating
/// that a schedule is well-formed before persistence. Kept separate from the service/store so
/// it can be unit-tested in isolation and reused by both the engine (for tick computation) and
/// CRUD operations (for validation and initial NextRunAt on create/update).
/// Uses the Cronos library for cron expression parsing — handles timezones and both 5 and 6 field expressions.
/// </summary>
internal static class ScheduleCalculator
{
    /// <summary>
    /// Computes the next occurrence after <paramref name="after"/> based on the schedule.
    /// Returns null if the task will never run again (e.g. one-shot already past).
    /// </summary>
    public static DateTimeOffset? ComputeNextRun(ScheduleConfig schedule, DateTimeOffset after)
    {
        if (schedule.Cron is not null)
        {
            var expression = CronExpression.Parse(schedule.Cron);
            var tz = schedule.Timezone is not null
                ? TimeZoneInfo.FindSystemTimeZoneById(schedule.Timezone)
                : TimeZoneInfo.Utc;
            var next = expression.GetNextOccurrence(after.UtcDateTime, tz);
            return next.HasValue ? new DateTimeOffset(next.Value, TimeSpan.Zero) : null;
        }

        if (schedule.IntervalMs is > 0)
        {
            return after.AddMilliseconds(schedule.IntervalMs.Value);
        }

        if (schedule.At is not null)
        {
            // One-shot: only return if it's in the future
            return schedule.At.Value > after ? schedule.At.Value : null;
        }

        return null;
    }

    /// <summary>
    /// Validates that exactly one schedule type is set. Returns an error message or null if valid.
    /// </summary>
    public static string? Validate(ScheduleConfig schedule)
    {
        var count = 0;
        if (schedule.Cron is not null) count++;
        if (schedule.IntervalMs is not null) count++;
        if (schedule.At is not null) count++;

        return count switch
        {
            0 => "Schedule must specify exactly one of: cron, intervalMs, or at.",
            1 => ValidateIndividual(schedule),
            _ => "Schedule must specify exactly one of: cron, intervalMs, or at. Multiple were set."
        };
    }

    private static string? ValidateIndividual(ScheduleConfig schedule)
    {
        if (schedule.Cron is not null)
        {
            try { CronExpression.Parse(schedule.Cron); }
            catch (CronFormatException ex) { return $"Invalid cron expression: {ex.Message}"; }
        }

        if (schedule.IntervalMs is <= 0)
            return "intervalMs must be a positive number.";

        if (schedule.Timezone is not null)
        {
            try { TimeZoneInfo.FindSystemTimeZoneById(schedule.Timezone); }
            catch (TimeZoneNotFoundException) { return $"Unknown timezone: {schedule.Timezone}"; }
        }

        return null;
    }
}
