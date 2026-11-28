using Microsoft.AspNetCore.Components;

namespace ChatStage.Samples.OverlayProbe;

public sealed class MeasuredChat : ChatStageChat
{
    [Inject] public ProbeState Probe { get; set; } = default!;

    protected override Task OnAfterRenderAsync(bool firstRender)
    {
        Probe.RootRenders++;
        return base.OnAfterRenderAsync(firstRender);
    }
}
