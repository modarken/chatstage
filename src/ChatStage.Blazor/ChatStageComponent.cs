using Bit.BlazorUI;
using Microsoft.AspNetCore.Components;

namespace ChatStage;

/// <summary>
/// Base class for your own Razor components shown inside the chat. Register one for a tool with
/// <see cref="ChatStageBuilder.AddToolComponent{TComponent, TData}(string)"/>, or for an AG-UI <c>CUSTOM</c> event with
/// <see cref="ChatStageBuilder.AddEventComponent{TComponent, TData}(string)"/>.
/// </summary>
/// <typeparam name="TData">
/// The component's data: the tool's JSON result, or the custom event's value, read with
/// <see cref="ChatStageBuilder.ConfigureJson"/>'s options.
/// </typeparam>
public abstract class ChatStageComponent<TData> : ComponentBase
{
    /// <summary>
    /// The data. For a tool, <see langword="null"/> until the tool has returned a result (show a placeholder meanwhile,
    /// and check <see cref="ToolCall"/> for a rejected or failed call).
    /// </summary>
    [Parameter]
    public TData? Data { get; set; }

    /// <summary>The tool call this component shows: its status, arguments and raw result. <see langword="null"/> for a custom event.</summary>
    [Parameter]
    public BitAiToolCallPart? ToolCall { get; set; }

    /// <summary>Talks back to the agent from the component, for example when the user presses a button on it.</summary>
    [Parameter, EditorRequired]
    public ChatStageActions Chat { get; set; } = default!;
}
