using Bit.BlazorUI;

namespace ChatStage;

// One Bit scheduler at the layout boundary; Markdown keeps its own incremental renderer unchanged.
internal sealed class ChatStageRenderSubscription : IAsyncDisposable
{
    private readonly ChatStageSessionContext _context;
    private readonly BitAiRenderScheduler _scheduler;
    private bool _generating;
    private bool _busy;
    private readonly bool _streamingLayout;

    internal ChatStageRenderSubscription(ChatStageSessionContext context, TimeSpan interval, Func<Task> render, bool streamingLayout = true)
    {
        _context = context;
        _scheduler = new(render, interval);
        _streamingLayout = streamingLayout;
        _busy = context.IsBusy;
        context.Changed += OnContextChanged;
        context.Conversation.Changed += OnConversationChanged;
        _generating = context.Session.IsGenerating;
        context.Session.StateChanged += OnStateChanged;
        context.Session.TurnCompleted += OnCompleted;
        context.Session.Processor.EventApplied += OnApplied;
    }

    internal int RenderCount => _scheduler.RenderCount;

    private void Request() => _scheduler.Request();

    private void OnConversationChanged(BitAiConversation conversation) => Request();

    private void OnContextChanged()
    {
        if (_busy != _context.IsBusy)
        {
            _busy = _context.IsBusy;
            Flush();
        }
        else if (_streamingLayout)
        {
            Request();
        }
    }

    private void Flush() => _ = _scheduler.FlushAsync();
    private void OnCompleted(BitAiMessage _) => Flush();

    private void OnStateChanged()
    {
        var generating = _context.Session.IsGenerating;
        if (generating != _generating)
        {
            _generating = generating;
            Flush();
        }
    }

    private void OnApplied(BitAiEvent evt)
    {
        if (evt is BitAiTextDeltaEvent or BitAiReasoningDeltaEvent or BitAiToolCallDeltaEvent or BitAiStructuredDataDeltaEvent)
        {
            if (_streamingLayout) Request();
        }
        else
        {
            Flush();
        }
    }

    public ValueTask DisposeAsync()
    {
        _context.Changed -= OnContextChanged;
        _context.Conversation.Changed -= OnConversationChanged;
        _context.Session.StateChanged -= OnStateChanged;
        _context.Session.TurnCompleted -= OnCompleted;
        _context.Session.Processor.EventApplied -= OnApplied;
        return _scheduler.DisposeAsync();
    }
}
