using Shared.Contracts;

namespace Api.Services;

public interface IWeatherService
{
    DailyForecastDto Get();
    DailyForecastDto Update(UpdateForecastRequest request);
    void Reset();
}

public sealed class WeatherService : IWeatherService
{
    private DailyForecastDto _forecast = DefaultForecast();

    public DailyForecastDto Get() => _forecast;

    public DailyForecastDto Update(UpdateForecastRequest request)
    {
        _forecast = new DailyForecastDto(request.Date, request.Segments);
        return _forecast;
    }

    public void Reset() => _forecast = DefaultForecast();

    private static DailyForecastDto DefaultForecast() =>
        new(
            DateOnly.FromDateTime(DateTime.Today),
            [
                new SegmentForecastDto(DaySegment.Morning, 8, PrecipitationKind.Drizzle, false),
                new SegmentForecastDto(DaySegment.Afternoon, 19, PrecipitationKind.None, true),
                new SegmentForecastDto(DaySegment.Evening, 13, PrecipitationKind.Rain, false)
            ]);
}
