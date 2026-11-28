using System.Net;
using System.Text.Json;
using AGUI.Abstractions;
using Bit.BlazorUI;

namespace ChatStage;

/// <summary>Conversions between Bit's conversation model and AG-UI's wire types.</summary>
internal static class AgUiMapping
{
    public static readonly JsonElement JsonNull = JsonElement.Parse("null");

    /// <summary>AG-UI's JSON: source-generated, camelCase, nulls left out.</summary>
    public static readonly JsonSerializerOptions Wire = new(JsonSerializerDefaults.Web)
    {
        TypeInfoResolver = AGUIJsonUtilities.DefaultTypeInfoResolver,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// The user messages after the agent's last reply: what a server that keeps the history hasn't seen yet. Messages the
    /// app showed (<see cref="ChatStageActions.ShowAsync{TData}"/>) are not replies, and are never sent.
    /// </summary>
    public static IEnumerable<IBitAiMessage> NewMessages(IReadOnlyList<IBitAiMessage> transcript)
    {
        var start = transcript.Count;
        while (start > 0 && !(transcript[start - 1].Role.IsAssistant && !ChatStageActions.IsAppMessage(transcript[start - 1])))
        {
            start--;
        }

        return transcript.Skip(start).Where(m => m.Role.IsUser);
    }

    /// <summary>
    /// Bit's transcript as AG-UI messages: user turns, assistant turns with their tool calls, and tool results. Messages the
    /// app showed are left out.
    /// </summary>
    public static List<AGUIMessage> ToAgUiMessages(IEnumerable<IBitAiMessage> transcript)
    {
        var list = new List<AGUIMessage>();
        foreach (var message in transcript)
        {
            if (ChatStageActions.IsAppMessage(message))
            {
                continue;
            }

            if (message.Role.IsUser)
            {
                list.Add(new AGUIUserMessage { Id = message.MessageId, Content = UserContent(message) });
                continue;
            }

            if (!message.Role.IsAssistant)
            {
                continue;
            }

            var calls = message.Parts.OfType<BitAiToolCallPart>().ToList();
            list.Add(new AGUIAssistantMessage
            {
                Id = message.MessageId,
                Content = string.Concat(message.Parts.OfType<BitAiTextPart>().Where(p => p is not BitAiReasoningPart).Select(p => p.Text)),
                ToolCalls = calls.Count == 0 ? null : [.. calls.Select(c => new AGUIToolCall
                {
                    Id = c.ToolCallId,
                    Type = "function",
                    Function = new AGUIToolCallFunction { Name = c.ToolName, Arguments = c.RawArguments ?? "{}" },
                })],
            });
            foreach (var call in calls)
            {
                var result = call.Result?.Value is { } value ? value.GetRawText()
                    : call.Result?.Text ?? (call.Status == BitAiToolCallStatus.Rejected ? "The user declined this tool call." : "");
                list.Add(new AGUIToolMessage { Id = "result-" + call.ToolCallId, ToolCallId = call.ToolCallId, Content = new AGUIContent(result) });
            }
        }

        return list;
    }

    private static AGUIContent UserContent(IBitAiMessage message)
    {
        if (message.Attachments.Count == 0)
        {
            return new AGUIContent(message.GetText());
        }

        var parts = new List<AGUIInputContent> { new AGUITextInputContent { Text = message.GetText() } };
        foreach (var attachment in message.Attachments)
        {
            AGUIInputContentSource source = attachment.Url is { Length: > 0 } url
                ? new AGUIInputContentUrlSource { Value = url, MimeType = attachment.ContentType }
                : attachment.Data is { } data
                    ? new AGUIInputContentDataSource { Value = Convert.ToBase64String(data.Span), MimeType = attachment.ContentType ?? "application/octet-stream" }
                    : throw new InvalidOperationException("Upload attachments or provide inline Data before submitting them to AG-UI.");
            AGUIMediaInputContent part = attachment.Kind switch
            {
                BitAiAttachmentKind.Image => new AGUIImageInputContent { Source = source },
                BitAiAttachmentKind.Audio => new AGUIAudioInputContent { Source = source },
                BitAiAttachmentKind.Video => new AGUIVideoInputContent { Source = source },
                _ => new AGUIDocumentInputContent { Source = source }
            };
            parts.Add(part);
        }

        return new AGUIContent(parts);
    }

    /// <summary>
    /// The answer to one approval interrupt. It carries only the decision and the tool call's id: the server runs the call
    /// it recorded. (The hosting treats a resume as a tool approval only when its payload has a <c>toolCall</c> with a
    /// <c>callId</c>.)
    /// </summary>
    public static AGUIResume Resume(AgUiTransport.PendingApproval approval, bool approved) => new()
    {
        InterruptId = approval.InterruptId,
        Status = ResumeStatus.Resolved,
        Payload = JsonSerializer.SerializeToElement(
            new AGUIToolApprovalResumePayload { Approved = approved, ToolCall = new AGUIToolCallInfo { CallId = approval.ToolCallId } },
            Wire),
    };

    public static BitAiToolOutcome ToOutcome(AGUIContent content)
    {
        var text = content.Value as string ?? string.Concat(content.Select(p => (p as AGUITextInputContent)?.Text));
        return ParseJson(text) is { } json
            ? BitAiToolOutcome.Success(json, json.ValueKind == JsonValueKind.String ? json.GetString() : text)
            : BitAiToolOutcome.FromText(text);
    }

    public static JsonElement? ParseJson(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        try
        {
            return JsonElement.Parse(text);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static BitAiError ToError(Exception exception) => exception switch
    {
        AgUiTransport.AgUiHttpException { Status: HttpStatusCode.Unauthorized } e => new BitAiError("The agent endpoint refused the sign-in.") { Kind = BitAiErrorKind.Authentication, Exception = e },
        AgUiTransport.AgUiHttpException { Status: HttpStatusCode.Forbidden } e => new BitAiError("You don't have access to this agent.") { Kind = BitAiErrorKind.Authorization, Exception = e },
        AgUiTransport.AgUiHttpException { Status: HttpStatusCode.TooManyRequests } e => new BitAiError("The agent is busy; try again shortly.") { Kind = BitAiErrorKind.RateLimit, IsRetryable = true, Exception = e },
        AgUiTransport.AgUiHttpException e => new BitAiError(e.Message) { Kind = BitAiErrorKind.Provider, IsRetryable = (int)e.Status >= 500, Exception = e },
        HttpRequestException or IOException => new BitAiError("Couldn't reach the agent.") { Kind = BitAiErrorKind.Network, IsRetryable = true, Exception = exception, Detail = exception.Message },
        _ => new BitAiError("The agent's reply couldn't be read.") { Kind = BitAiErrorKind.Provider, Exception = exception, Detail = exception.Message },
    };
}
