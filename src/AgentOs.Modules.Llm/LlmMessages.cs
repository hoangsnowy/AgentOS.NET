// The user turn of an LLM request as Microsoft.Extensions.AI content: the prompt text, then each image (the Designer
// agent's screenshots — ADR-0016) as DataContent, which the Anthropic and OpenAI chat clients send as image blocks.
// One place so every HTTP-backed provider carries images the same way.

using System;
using System.Collections.Generic;
using AgentOs.Domain.Llm;
using Microsoft.Extensions.AI;

namespace AgentOs.Modules.Llm;

/// <summary>Builds the chat messages of an <see cref="LlmRequest"/>.</summary>
public static class LlmMessages
{
    /// <summary>The system turn (when there is one) and the user turn — text plus every image.</summary>
    public static List<ChatMessage> For(LlmRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var messages = new List<ChatMessage>(2);
        if (!string.IsNullOrEmpty(request.SystemPrompt))
        {
            messages.Add(new ChatMessage(ChatRole.System, request.SystemPrompt));
        }
        messages.Add(User(request));
        return messages;
    }

    /// <summary>The user turn: the prompt text, then each image in order.</summary>
    public static ChatMessage User(LlmRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Images is not { Count: > 0 } images) { return new ChatMessage(ChatRole.User, request.UserPrompt); }
        var contents = new List<AIContent>(images.Count + 1) { new TextContent(request.UserPrompt) };
        foreach (var image in images)
        {
            contents.Add(new DataContent(image.Data, image.MediaType) { Name = image.Name });
        }
        return new ChatMessage(ChatRole.User, contents);
    }
}
