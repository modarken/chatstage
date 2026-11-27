using System.Text.Json;
using Bit.BlazorUI;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;

namespace ChatStage;

/// <summary>
/// The app's own components, keyed by tool name and by AG-UI custom event name. Filled by <see cref="ChatStageBuilder"/>
/// and applied to Bit's renderer registry when that is first resolved.
/// </summary>
internal sealed class ChatStageComponentRegistry
{
    private readonly Dictionary<string, Registration> _tools = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Registration> _events = new(StringComparer.Ordinal);

    internal sealed record Registration(Type Component, Type Data);

    public void AddTool(string toolName, Type component, Type data) => Add(_tools, toolName, component, data, "tool");

    public void AddEvent(string eventName, Type component, Type data) => Add(_events, eventName, component, data, "custom event");

    /// <summary>Whether an AG-UI custom event of this name is shown as a part (otherwise it is passed through as an event).</summary>
    public bool HasEvent(string eventName) => _events.ContainsKey(eventName);

    /// <summary>How component data is read from tool results and custom event values. Default: web defaults (camelCase).</summary>
    public JsonSerializerOptions Json { get; } = new(JsonSerializerDefaults.Web);

    public void Apply(BitAiRendererRegistry renderers)
    {
        var json = Json;
        foreach (var (toolName, registration) in _tools)
        {
            renderers.RegisterTool(toolName, context => builder =>
            {
                var data = context.Call.Result is { IsSuccess: true, Value: { } value } ? Read(value, registration.Data, json) : null;
                Render(builder, registration, data, context.Call, context.Session);
            });
        }

        if (_events.Count == 0)
        {
            return;
        }

        // Custom events arrive as structured-data parts named after the event; anything else keeps Bit's own view.
        var events = new Dictionary<string, Registration>(_events, StringComparer.Ordinal);
        renderers.RegisterPart(BitAiMessagePartKinds.StructuredData, context => builder =>
        {
            var part = (BitAiStructuredDataPart)context.Part;
            if (part.SchemaName is { } name && events.TryGetValue(name, out var registration))
            {
                var data = part.Value is { } value ? Read(value, registration.Data, json) : null;
                Render(builder, registration, data, toolCall: null, context.Session);
                return;
            }

            builder.OpenComponent<BitAiStructuredDataView>(0);
            builder.AddComponentParameter(1, nameof(BitAiStructuredDataView.Part), part);
            builder.AddComponentParameter(2, nameof(BitAiStructuredDataView.Message), context.Message);
            builder.CloseComponent();
        });
    }

    private static void Add(Dictionary<string, Registration> map, string name, Type component, Type data, string what)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (!map.TryAdd(name, new Registration(component, data)))
        {
            throw new InvalidOperationException($"A component is already registered for the {what} '{name}'.");
        }
    }

    private static object? Read(JsonElement value, Type type, JsonSerializerOptions json) =>
        type == typeof(JsonElement) ? value : value.Deserialize(type, json);

    private void Render(RenderTreeBuilder builder, Registration registration, object? data, BitAiToolCallPart? toolCall, BitAiChatSession? session)
    {
        builder.OpenComponent(0, registration.Component);
        builder.AddComponentParameter(1, nameof(ChatStageComponent<object>.Data), data);
        builder.AddComponentParameter(2, nameof(ChatStageComponent<object>.ToolCall), toolCall);
        builder.AddComponentParameter(3, nameof(ChatStageComponent<object>.Chat), ChatStageActions.For(session, this));
        builder.CloseComponent();
    }
}
