namespace Api.Services;

/// <summary>
/// An HTTP message handler that retries failed requests with exponential backoff.
/// </summary>
public class RetryHttpMessageHandler : DelegatingHandler
{
    private readonly int _maxRetries;
    private readonly TimeSpan _initialDelay;

    public RetryHttpMessageHandler(int maxRetries = 3, TimeSpan? initialDelay = null)
        : base(new HttpClientHandler())
    {
        _maxRetries = maxRetries;
        _initialDelay = initialDelay ?? TimeSpan.FromMilliseconds(100);
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        int attempt = 0;
        TimeSpan delay = _initialDelay;

        while (true)
        {
            try
            {
                var response = await base.SendAsync(request, cancellationToken);

                // Retry on server errors (5xx) or request timeout (408)
                if ((int)response.StatusCode >= 500 || response.StatusCode == System.Net.HttpStatusCode.RequestTimeout)
                {
                    if (attempt < _maxRetries)
                    {
                        response.Dispose();
                        attempt++;
                        await Task.Delay(delay, cancellationToken);
                        delay = TimeSpan.FromMilliseconds(delay.TotalMilliseconds * 2);
                        continue;
                    }
                }

                return response;
            }
            catch (HttpRequestException) when (attempt < _maxRetries)
            {
                attempt++;
                await Task.Delay(delay, cancellationToken);
                delay = TimeSpan.FromMilliseconds(delay.TotalMilliseconds * 2);
            }
            catch (TimeoutException) when (attempt < _maxRetries)
            {
                attempt++;
                await Task.Delay(delay, cancellationToken);
                delay = TimeSpan.FromMilliseconds(delay.TotalMilliseconds * 2);
            }
        }
    }
}
