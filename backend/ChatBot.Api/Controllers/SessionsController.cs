using System.Text;
using System.Text.Json;
using ChatBot.Api.Chat;
using ChatBot.Api.Contracts;
using ChatBot.Api.Llm;
using ChatBot.Api.Sessions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace ChatBot.Api.Controllers;

[ApiController]
[Route("api/sessions")]
public sealed class SessionsController(
    ISessionStore sessions,
    ChatService chat,
    ILanguageModel model,
    IOptions<ChatOptions> chatOptions,
    ILogger<SessionsController> logger) : ControllerBase
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly ChatOptions _chatOptions = chatOptions.Value;

    // "A new session must not inherit the previous one's history" is implemented here and
    // nowhere else: a new id maps to a new empty message list, and nothing in the system
    // is keyed by anything but that id.
    [HttpPost]
    [ProducesResponseType<SessionSummaryDto>(StatusCodes.Status201Created)]
    public ActionResult<SessionSummaryDto> Create()
    {
        var session = sessions.Create();
        logger.LogInformation("Created session {SessionId}.", session.Id);

        return CreatedAtAction(nameof(Get), new { id = session.Id }, SessionSummaryDto.From(session));
    }

    [HttpGet]
    [ProducesResponseType<IReadOnlyList<SessionSummaryDto>>(StatusCodes.Status200OK)]
    public ActionResult<IReadOnlyList<SessionSummaryDto>> List()
        => Ok(sessions.List().Select(SessionSummaryDto.From).ToArray());

    [HttpGet("{id:guid}")]
    [ProducesResponseType<SessionDetailDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<SessionDetailDto> Get(Guid id)
        => sessions.TryGet(id, out var session)
            ? Ok(SessionDetailDto.From(session))
            : NotFound(Problem($"Session '{id}' does not exist or has expired.", statusCode: 404).Value);

    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public IActionResult Delete(Guid id)
        => sessions.Delete(id) ? NoContent() : NotFound();

    // Streams the reply as SSE. A 200-token answer takes 15-20s on CPU and waiting for
    // that in silence feels broken.
    //
    // SSE rather than WebSockets because the traffic is one-way and short-lived. Written
    // straight to Response.Body rather than returned as an IActionResult because the
    // framework result types buffer, which is the one thing we're trying to avoid.
    [HttpPost("{id:guid}/messages")]
    [Produces("text/event-stream")]
    public async Task SendMessage(Guid id, [FromBody] SendMessageRequest request, CancellationToken cancellationToken)
    {
        if (!model.IsReady)
        {
            await WriteProblemAsync(StatusCodes.Status503ServiceUnavailable,
                model.Info.LoadError ?? "The language model is still loading. Try again in a moment.");
            return;
        }

        var message = request.Message?.Trim() ?? "";
        if (message.Length == 0)
        {
            await WriteProblemAsync(StatusCodes.Status400BadRequest, "Message cannot be empty.");
            return;
        }

        if (message.Length > _chatOptions.MaxUserMessageCharacters)
        {
            await WriteProblemAsync(StatusCodes.Status400BadRequest,
                $"Message exceeds the {_chatOptions.MaxUserMessageCharacters:N0} character limit.");
            return;
        }

        Response.Headers.ContentType = "text/event-stream";
        Response.Headers.CacheControl = "no-cache";
        Response.Headers.Connection = "keep-alive";
        // Otherwise a reverse proxy buffers the whole stream and delivers it in one lump.
        Response.Headers["X-Accel-Buffering"] = "no";

        try
        {
            await foreach (var evt in chat.SendAsync(id, message, cancellationToken))
            {
                switch (evt)
                {
                    case ChatStreamEvent.ContextPlanned(var plan):
                        await SendEventAsync("context", new ContextInfoDto(
                            plan.IncludedMessages, plan.DroppedMessages, plan.PromptTokens,
                            plan.ResponseBudget, plan.ContentTruncated));
                        break;

                    case ChatStreamEvent.Chunk(var text):
                        // JSON-encoded, not raw: SSE ends a record at a blank line, and any
                        // markdown reply contains one.
                        await SendEventAsync("chunk", new { text });
                        break;

                    case ChatStreamEvent.Completed(var msg, var tokens, var elapsed, var rate):
                        await SendEventAsync("done", new CompletionInfoDto(msg.Id, tokens, elapsed, rate));
                        break;
                }
            }
        }
        catch (SessionNotFoundException ex)
        {
            await SendEventAsync("error", new { message = ex.Message, code = "session_not_found" });
        }
        catch (OperationCanceledException)
        {
            // Client hung up; ChatService has already stored the partial reply.
            logger.LogInformation("Client cancelled generation for session {SessionId}.", id);
        }
        catch (Exception ex)
        {
            // Headers are already on the wire, so the failure has to be reported inside
            // the stream rather than as a status code.
            logger.LogError(ex, "Generation failed for session {SessionId}.", id);
            await SendEventAsync("error", new { message = "Generation failed.", code = "inference_error" });
        }
    }

    private async Task SendEventAsync(string eventName, object payload)
    {
        var json = JsonSerializer.Serialize(payload, Json);
        await Response.WriteAsync($"event: {eventName}\ndata: {json}\n\n", Encoding.UTF8);
        await Response.Body.FlushAsync();
    }

    private async Task WriteProblemAsync(int statusCode, string detail)
    {
        Response.StatusCode = statusCode;
        Response.ContentType = "application/problem+json";
        await Response.WriteAsync(JsonSerializer.Serialize(new { status = statusCode, detail }, Json));
    }
}
