using ChatBot.Api.Contracts;
using ChatBot.Api.Llm;
using ChatBot.Api.Rag;
using Microsoft.AspNetCore.Mvc;

namespace ChatBot.Api.Controllers;

// Lets the UI tell "still loading" apart from "backend is broken". The client polls this
// on startup and keeps the composer disabled until Ready (and IndexReady, with RAG on).
[ApiController]
[Route("api/model")]
public sealed class ModelController(ILanguageModel model, RagRuntime rag) : ControllerBase
{
    [HttpGet("status")]
    [ProducesResponseType<ModelStatusDto>(StatusCodes.Status200OK)]
    public ActionResult<ModelStatusDto> Status()
    {
        var info = model.Info;
        var documents = rag.Documents;
        var ready = documents.Where(d => d.Status == DocumentStatus.Ready).ToList();

        return Ok(new ModelStatusDto(
            info.Name, info.Ready, info.ContextSize, info.ParameterCount, info.LoadError,
            RagEnabled: rag.Enabled,
            IndexReady: !rag.Enabled || rag.IsReady,
            EmbeddingModel: rag.Enabled ? rag.EmbeddingModelName : null,
            DocumentTitle: ready.Count == 1 ? ready[0].Title : null,
            IndexError: rag.LoadError,
            DocumentCount: ready.Count,
            PendingDocuments: documents.Count(d => d.Status is DocumentStatus.Queued or DocumentStatus.Processing)));
    }
}
