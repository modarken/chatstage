using System.Diagnostics;
using System.Runtime.CompilerServices;
using Bit.BlazorUI;

namespace ChatStage.Samples.OverlayProbe;

public sealed class ProbeState
{
    public BitAiConversation Conversation { get; } = new("probe");
    public TaskCompletionSource<BitAiChatSession> Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public bool Before { get; init; }
    public bool ThreadUpdates { get; init; }
    public int Deltas { get; set; }
    public int RootRenders { get; set; }
}

public sealed class ProbeTransport(ProbeState state) : IBitAiTransport
{
    public async IAsyncEnumerable<BitAiEvent> StreamAsync(BitAiRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        yield return new BitAiMessageStartedEvent(BitAiRole.Assistant);
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < TimeSpan.FromSeconds(5))
        {
            if (state.ThreadUpdates) state.Conversation.SetTitle($"Chunk {state.Deltas}");
            state.Deltas++;
            yield return new BitAiTextDeltaEvent("word ");
            await Task.Delay(2, cancellationToken);
        }
        yield return new BitAiMessageCompletedEvent(BitAiFinishReason.Stop);
    }
}
