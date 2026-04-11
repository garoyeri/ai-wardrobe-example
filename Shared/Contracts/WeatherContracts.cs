namespace Shared.Contracts;

public enum DaySegment
{
    Morning,
    Afternoon,
    Evening
}

public enum PrecipitationKind
{
    None,
    Drizzle,
    Rain,
    Snow
}

public sealed record SegmentForecastDto(
    DaySegment Segment,
    int TemperatureC,
    PrecipitationKind Precipitation,
    bool IsSunny);

public sealed record DailyForecastDto(
    DateOnly Date,
    IReadOnlyList<SegmentForecastDto> Segments);

public sealed record UpdateForecastRequest(
    DateOnly Date,
    IReadOnlyList<SegmentForecastDto> Segments);
