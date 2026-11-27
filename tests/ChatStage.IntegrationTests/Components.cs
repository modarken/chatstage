using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;

namespace ChatStage.IntegrationTests;

/// <summary>A minimal tool component, as an app would write one.</summary>
internal sealed class ListingCard : ChatStageComponent<AgentServer.Listing>
{
    protected override void BuildRenderTree(RenderTreeBuilder builder) => builder.AddContent(0, Data?.Title);
}

/// <summary>A minimal custom-event component.</summary>
internal sealed class InvoiceCard : ChatStageComponent<InvoiceCard.Invoice>
{
    public sealed record Invoice(string Number, decimal Total);

    protected override void BuildRenderTree(RenderTreeBuilder builder) => builder.AddContent(0, Data?.Number);
}
