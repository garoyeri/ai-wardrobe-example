namespace Api.Services;

/// <summary>
/// Manages CancellationTokenSource instances for ongoing conversations.
/// Allows frontend to request cancellation of a specific conversation thread.
/// </summary>
public interface IConversationCancellationManager
{
    /// <summary>
    /// Gets or creates a CancellationTokenSource for a conversation.
    /// </summary>
    CancellationTokenSource GetOrCreateSource(string conversationId);

    /// <summary>
    /// Requests cancellation of a conversation.
    /// </summary>
    /// <returns>True if cancellation was requested, false if conversation not found.</returns>
    bool RequestCancel(string conversationId);

    /// <summary>
    /// Cleans up resources for a completed conversation.
    /// </summary>
    void Cleanup(string conversationId);
}

public sealed class ConversationCancellationManager : IConversationCancellationManager
{
    private readonly Dictionary<string, CancellationTokenSource> _sources = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _gate = new();

    public CancellationTokenSource GetOrCreateSource(string conversationId)
    {
        lock (_gate)
        {
            if (_sources.TryGetValue(conversationId, out var existing))
            {
                // If the existing source was already cancelled, create a new one
                if (existing.Token.IsCancellationRequested)
                {
                    existing.Dispose();
                    existing = new CancellationTokenSource();
                    _sources[conversationId] = existing;
                }
                return existing;
            }

            var source = new CancellationTokenSource();
            _sources[conversationId] = source;
            return source;
        }
    }

    public bool RequestCancel(string conversationId)
    {
        lock (_gate)
        {
            if (_sources.TryGetValue(conversationId, out var source) && !source.Token.IsCancellationRequested)
            {
                source.Cancel();
                return true;
            }
            return false;
        }
    }

    public void Cleanup(string conversationId)
    {
        lock (_gate)
        {
            if (_sources.Remove(conversationId, out var source))
            {
                source.Dispose();
            }
        }
    }
}
