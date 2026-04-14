using Shared.Contracts;

namespace Api.Services;

public interface IOutfitRecommendationService
{
    OutfitRecommendationDto Recommend(ChatRequest request, IReadOnlyList<ClosetItemDto> closet, DailyForecastDto forecast, string agentExplanation);
}

public sealed class OutfitRecommendationService : IOutfitRecommendationService
{
    public OutfitRecommendationDto Recommend(ChatRequest request, IReadOnlyList<ClosetItemDto> closet, DailyForecastDto forecast, string agentExplanation)
    {
        var warnings = new List<string>();
        var reasons = new List<string>();

        var minimumTemp = forecast.Segments.Min(s => s.TemperatureC);
        var hasRain = forecast.Segments.Any(s => s.Precipitation is PrecipitationKind.Rain or PrecipitationKind.Drizzle or PrecipitationKind.Snow);
        var hasSun = forecast.Segments.Any(s => s.IsSunny);

        var hybrid = closet
            .Where(HasRole(OutfitRole.Top, OutfitRole.Bottom))
            .OrderByDescending(x => FitWarmthScore(x, minimumTemp))
            .FirstOrDefault();

        ClosetItemDto? top;
        ClosetItemDto? bottom;
        var useHybrid = false;

        if (hybrid is not null)
        {
            top = hybrid;
            bottom = hybrid;
            useHybrid = true;
            reasons.Add($"Selected {hybrid.Name} as a hybrid top/bottom item.");
        }
        else
        {
            top = closet.Where(HasRole(OutfitRole.Top)).OrderByDescending(x => FitWarmthScore(x, minimumTemp)).FirstOrDefault();
            bottom = closet.Where(HasRole(OutfitRole.Bottom)).OrderByDescending(x => FitWarmthScore(x, minimumTemp)).FirstOrDefault();
        }

        var shoes = closet.Where(HasRole(OutfitRole.Shoes)).OrderByDescending(x => x.Waterproof ? 1 : 0).FirstOrDefault();

        ClosetItemDto? jacket = null;
        if (hasRain || minimumTemp <= 10)
        {
            jacket = closet
                .Where(HasRole(OutfitRole.Jacket))
                .OrderByDescending(x => x.Waterproof ? 2 : 0)
                .ThenByDescending(x => x.Warmth)
                .FirstOrDefault();

            if (hasRain && jacket is not null && !jacket.Waterproof)
            {
                warnings.Add("Rain is expected and no waterproof jacket was found. Consider adding one.");
            }

            if (jacket is not null)
            {
                reasons.Add($"Included {jacket.Name} for {(hasRain ? "precipitation" : "cold weather")} coverage.");
            }
        }

        ClosetItemDto? hat = null;
        if (hasSun)
        {
            hat = closet.Where(HasRole(OutfitRole.Hat)).OrderByDescending(x => x.Weight == MaterialWeight.Light ? 1 : 0).FirstOrDefault();
            if (hat is not null)
            {
                reasons.Add($"Suggested {hat.Name} because sunny periods are expected.");
            }
        }

        if (!request.BoldMode && top is not null && bottom is not null && top.Id != bottom.Id && PatternsClash(top, bottom))
        {
            warnings.Add("Top and bottom patterns may clash. Enable bold mode if this is intentional.");
        }

        if (top is null || bottom is null || shoes is null)
        {
            warnings.Add("Closet is missing one or more required slots: top, bottom, shoes.");
        }

        if (minimumTemp <= 8)
        {
            reasons.Add("Morning low temperature is cold, so warmth and layering were prioritized.");
        }

        if (hasRain)
        {
            reasons.Add("Precipitation appears in the day forecast, so weather resistance was prioritized.");
        }

        var selection = new OutfitSelectionDto(top, bottom, shoes, hat, jacket, useHybrid);
        return new OutfitRecommendationDto(selection, warnings, reasons, agentExplanation);
    }

    private static Func<ClosetItemDto, bool> HasRole(params OutfitRole[] roles) =>
        item => roles.All(r => item.Roles.Contains(r));

    private static int FitWarmthScore(ClosetItemDto item, int minTemp)
    {
        var target = minTemp <= 8 ? 4 : minTemp <= 15 ? 3 : 2;
        return 5 - Math.Abs(item.Warmth - target);
    }

    private static bool PatternsClash(ClosetItemDto top, ClosetItemDto bottom)
    {
        var topPattern = top.Patterns.FirstOrDefault("solid").ToLowerInvariant();
        var bottomPattern = bottom.Patterns.FirstOrDefault("solid").ToLowerInvariant();
        return topPattern != "solid" && bottomPattern != "solid" && topPattern != bottomPattern;
    }
}
