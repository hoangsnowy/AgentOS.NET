// AgentOs.Application/Prompts/RequirementPrompt.cs
// Sprint 3 — extracted the prompt from RequirementAgent. Version v1.

using AgentOs.Domain.Llm;
using AgentOs.Domain.Pipeline;

namespace AgentOs.Modules.Pipeline.Prompts;

/// <summary>System + User template for the Requirement Agent (v1).</summary>
public static class RequirementPrompt
{
    /// <summary>Prompt version. Bump when editing <see cref="System"/> or <see cref="RenderUser"/>.</summary>
    public const string Version = "v1.1";   // v1.1: untrusted-input rule (ticket stories are fenced data)

    /// <summary>System prompt.</summary>
    public const string System = """
        You are the Requirement Agent in the Agentic SDLC system.
        Task: analyze a user story and return a specification as JSON.

        Return ONLY JSON (no markdown fence, no prose) following the schema:
        {
          "title": "Short title (≤ 80 characters)",
          "summary": "1-2 sentence summary",
          "stakeholders": ["string"],
          "functionalRequirements": ["string"],
          "nonFunctionalRequirements": ["string"],
          "entities": [
            { "name": "PascalCase", "fields": ["fieldName: Type"], "notes": "optional" }
          ],
          "endpoints": [
            { "method": "GET|POST|PUT|DELETE|PATCH", "path": "/route", "purpose": "description", "authRequired": false }
          ],
          "acceptanceCriteria": ["string"]
        }

        Rules:
        - MUST have ≥ 1 entity, ≥ 1 endpoint, ≥ 3 acceptance criteria.
        - Use English for all field text except the entity name (English PascalCase) and the HTTP method.
        - Do NOT include comments, do NOT use a trailing comma.
        """ + "\n" + UntrustedText.SystemRule;

    /// <summary>Renders the user prompt from the user story.</summary>
    public static string RenderUser(UserStory story)
    {
        global::System.ArgumentNullException.ThrowIfNull(story);
        return $"""
            User story (locale {story.Locale}):
            {story.Description}

            Generate the requirement specification as JSON that matches the schema.
            """;
    }
}
