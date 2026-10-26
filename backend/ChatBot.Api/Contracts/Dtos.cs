using System.ComponentModel.DataAnnotations;
using ChatBot.Api.Domain;

namespace ChatBot.Api.Contracts;

// Wire shapes, kept separate from the domain types so renaming a domain property can't
// silently break the Angular client.

public sealed record MessageDto(Guid Id, string Role, string Content, DateTimeOffset CreatedAt)
{
    public static MessageDto From(ChatMessage message) =>
        new(message.Id, message.Role.ToString().ToLowerInvariant(), message.Content, message.CreatedAt);
}

public sealed record SessionSummaryDto(
    Guid Id,
    string? Title,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastActivityAt,
    int MessageCount)
{
    public static SessionSummaryDto From(ChatSession session) =>
        new(session.Id, session.Title, session.CreatedAt, session.LastActivityAt, session.MessageCount);
}

public sealed record SessionDetailDto(
    Guid Id,
    string? Title,
    DateTimeOffset CreatedAt,
    IReadOnlyList<MessageDto> Messages)
{
    public static SessionDetailDto From(ChatSession session) =>
        new(session.Id, session.Title, session.CreatedAt,
            session.Snapshot().Select(MessageDto.From).ToArray());
}

public sealed class SendMessageRequest
{
    [Required(AllowEmptyStrings = false, ErrorMessage = "A message is required.")]
    public string Message { get; set; } = "";
}

/// <summary>PromptPlan minus the prompt text itself.</summary>
public sealed record ContextInfoDto(
    int IncludedMessages,
    int DroppedMessages,
    int PromptTokens,
    int ResponseBudget,
    bool ContentTruncated);

public sealed record CompletionInfoDto(
    Guid MessageId,
    int CompletionTokens,
    double ElapsedSeconds,
    double TokensPerSecond);

public sealed record ModelStatusDto(
    string Model,
    bool Ready,
    uint ContextSize,
    ulong ParameterCount,
    string? Error);
