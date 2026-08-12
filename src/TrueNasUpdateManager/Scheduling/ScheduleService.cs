using Cronos;
using TrueNasUpdateManager.Domain;

namespace TrueNasUpdateManager.Scheduling;

public interface IScheduleService
{
    ScheduleValidationResult Validate(string? expression, string? timeZoneId, DateTimeOffset? from = null);
    DateTimeOffset? GetNextRun(string expression, string timeZoneId, DateTimeOffset from);
}

public sealed class ScheduleService(TimeProvider timeProvider) : IScheduleService
{
    public ScheduleValidationResult Validate(
        string? expression,
        string? timeZoneId,
        DateTimeOffset? from = null)
    {
        if (string.IsNullOrWhiteSpace(expression))
        {
            return Invalid("Enter a 5-field cron expression.");
        }

        if (expression.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length != 5)
        {
            return Invalid("Cron expressions must contain exactly 5 fields; seconds are not supported.");
        }

        if (string.IsNullOrWhiteSpace(timeZoneId))
        {
            return Invalid("Choose an IANA timezone.");
        }

        try
        {
            var cron = CronExpression.Parse(expression, CronFormat.Standard);
            var timeZone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
            var cursor = from ?? timeProvider.GetUtcNow();
            var runs = new List<DateTimeOffset>(3);
            for (var index = 0; index < 3; index++)
            {
                var next = cron.GetNextOccurrence(cursor, timeZone, inclusive: false);
                if (next is null)
                {
                    break;
                }

                runs.Add(next.Value);
                cursor = next.Value;
            }

            return new ScheduleValidationResult(
                true,
                null,
                runs,
                BuildPreview(expression, timeZoneId, runs));
        }
        catch (CronFormatException exception)
        {
            return Invalid(exception.Message);
        }
        catch (TimeZoneNotFoundException)
        {
            return Invalid("The selected IANA timezone is not installed.");
        }
        catch (InvalidTimeZoneException)
        {
            return Invalid("The selected timezone is invalid.");
        }
    }

    public DateTimeOffset? GetNextRun(string expression, string timeZoneId, DateTimeOffset from)
    {
        var cron = CronExpression.Parse(expression, CronFormat.Standard);
        var timeZone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        return cron.GetNextOccurrence(from, timeZone, inclusive: false);
    }

    private static ScheduleValidationResult Invalid(string error) =>
        new(false, error, [], string.Empty);

    private static string BuildPreview(
        string expression,
        string timeZoneId,
        IReadOnlyList<DateTimeOffset> runs) =>
        runs.Count == 0
            ? $"{expression} in {timeZoneId}"
            : $"Next: {string.Join(", ", runs.Select(run => run.ToString("ddd, MMM d HH:mm zzz")))}";
}
