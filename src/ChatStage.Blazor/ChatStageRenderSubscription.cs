using Bit.BlazorUI;

namespace ChatStage;

// Renders a layout root (ChatStageChat, ChatStageSession) through one Bit scheduler. Streamed deltas never render the
// root: Bit's part views render themselves. Structural changes (a message added, the title) are coalesced; a change of
// busy state, a tool, approval, error, usage or completion event, and the end of a turn render at once.
internal sealed class ChatStageRenderSubscription : IAsyncDisposable
{
    private readonly ChatStageSessionContext _context;
    private readonly BitAiRenderScheduler _scheduler;
    private bool _busy;
    private int _disposed;

    internal ChatStageRenderSubscription(ChatStageSessionContext context, TimeSpan interval, Func<Task> render)
    {
        _context = context;
        _scheduler = new(render, interval);
        _busy = context.IsBusy;
        context.Changed += OnContextChanged;
        context.Conversation.Changed += OnConversationChanged;
        context.Session.TurnCompleted += OnCompleted;
        context.Session.ApprovalDecided += OnApprovalDecided;
        context.Session.Processor.EventApplied += OnApplied;
    }

    internal int RenderCount => _scheduler.RenderCount;

    private void OnConversationChanged(BitAiConversation conversation) => Request();

    private void OnContextChanged()
    {
        var busy = _context.IsBusy;
        if (_busy != busy)
        {
            _busy = busy;
            Flush();
        }
    }

    private void OnCompleted(BitAiMessage _) => Flush();

    private void OnApprovalDecided(BitAiToolCallPart part, BitAiApprovalDecision decision) => Flush();

    private void OnApplied(BitAiEvent evt)
    {
        if (evt is not (BitAiTextDeltaEvent or BitAiReasoningDeltaEvent or BitAiToolCallDeltaEvent or BitAiStructuredDataDeltaEvent))
        {
            Flush();
        }
    }

    private void Request()
    {
        if (Volatile.Read(ref _disposed) == 0)
        {
            _scheduler.Request();
        }
    }

    private void Flush()
    {
        if (Volatile.Read(ref _disposed) == 0)
        {
            _ = _scheduler.FlushAsync();
        }
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return ValueTask.CompletedTask;
        }

        _context.Changed -= OnContextChanged;
        _context.Conversation.Changed -= OnConversationChanged;
        _context.Session.TurnCompleted -= OnCompleted;
        _context.Session.ApprovalDecided -= OnApprovalDecided;
        _context.Session.Processor.EventApplied -= OnApplied;
        return _scheduler.DisposeAsync();
    }
}
