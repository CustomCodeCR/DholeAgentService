using Dhole.Agent.Infrastructure.Providers.Maersk.Models;
using Microsoft.Playwright;

namespace Dhole.Agent.Infrastructure.Providers.Maersk.Network;

public sealed class MaerskOfferInterceptor
{
    private TaskCompletionSource<CapturedMaerskOfferResponse>? _capture;

    public Task<CapturedMaerskOfferResponse> WaitForOfferAsync(IPage page, TimeSpan timeout, CancellationToken cancellationToken)
    {
        _capture = new(TaskCreationOptions.RunContinuationsAsynchronously);

        page.Response += OnResponse;

        var timeoutTask = Task.Delay(timeout, cancellationToken).ContinueWith(
            _ => throw new TimeoutException("Maersk departures/offers response was not captured."),
            cancellationToken,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        return AwaitAndDetachAsync(page, _capture.Task, timeoutTask);
    }

    private async Task OnResponse(object? sender, IResponse response)
    {
        if (_capture is null || _capture.Task.IsCompleted)
            return;

        if (!response.Url.Contains("/v2/departures/offers", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(response.Request.Method, "POST", StringComparison.OrdinalIgnoreCase))
            return;

        try
        {
            var headers = await response.AllHeadersAsync();
            headers.TryGetValue("correlation-id", out var correlationId);
            if (string.IsNullOrWhiteSpace(correlationId))
                headers.TryGetValue("x-correlation-id", out correlationId);

            var json = await response.TextAsync();
            _capture.TrySetResult(new CapturedMaerskOfferResponse(
                response.Request.PostData,
                response.Status,
                correlationId,
                json));
        }
        catch (Exception ex)
        {
            _capture.TrySetException(ex);
        }
    }

    private async Task<CapturedMaerskOfferResponse> AwaitAndDetachAsync(
        IPage page,
        Task<CapturedMaerskOfferResponse> capture,
        Task timeoutTask)
    {
        try
        {
            var completed = await Task.WhenAny(capture, timeoutTask);
            if (completed == timeoutTask)
                await timeoutTask;
            return await capture;
        }
        finally
        {
            page.Response -= OnResponse;
        }
    }
}
