using ChatBot.Api.Contracts;
using ChatBot.Api.Rag;
using Microsoft.AspNetCore.Mvc;

namespace ChatBot.Api.Controllers;

// The document library: what is loaded and in what state, uploads, the full text behind
// a citation, the PDF itself, and a retrieval-only search for checking what a question
// would find without waiting for the model to write an answer.
[ApiController]
[Route("api/documents")]
public sealed class DocumentsController(RagRuntime rag, ILogger<DocumentsController> logger) : ControllerBase
{
    // Hard ceiling for the request; Rag:MaxUploadMegabytes is checked inside and is what
    // the user is told about.
    private const long RequestCeiling = 200L * 1024 * 1024;

    [HttpGet]
    [ProducesResponseType<IReadOnlyList<DocumentInfoDto>>(StatusCodes.Status200OK)]
    public ActionResult<IReadOnlyList<DocumentInfoDto>> List() =>
        Ok(rag.Documents.Select(DocumentInfoDto.From).ToArray());

    [HttpGet("{docId}")]
    [ProducesResponseType<DocumentInfoDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<DocumentInfoDto> Get(string docId) =>
        rag.Find(docId) is { } d ? Ok(DocumentInfoDto.From(d)) : NotFound();

    // multipart/form-data with one "file" field. Returns 202: the document is queued and
    // its status moves Queued -> Processing -> Ready (or Failed) on GET /api/documents.
    [HttpPost]
    [RequestSizeLimit(RequestCeiling)]
    [RequestFormLimits(MultipartBodyLengthLimit = RequestCeiling)]
    [ProducesResponseType<DocumentInfoDto>(StatusCodes.Status202Accepted)]
    [ProducesResponseType<DocumentInfoDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<DocumentInfoDto>> Upload(IFormFile? file, CancellationToken cancellationToken)
    {
        if (!rag.Enabled || !rag.IsReady)
        {
            return Problem(rag.LoadError ?? "The document library is still loading.", statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        if (file is null || file.Length == 0)
        {
            return Problem("Choose a PDF file to upload.", statusCode: StatusCodes.Status400BadRequest);
        }

        var limit = rag.Options.MaxUploadMegabytes * 1024L * 1024;
        if (file.Length > limit)
        {
            return Problem($"The file is {file.Length / 1024d / 1024:N1} MB; the limit is {rag.Options.MaxUploadMegabytes} MB.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        try
        {
            var known = rag.Documents.Select(d => d.DocId).ToHashSet();
            await using var stream = file.OpenReadStream();
            var document = await rag.AddUploadAsync(stream, file.FileName, cancellationToken);

            var dto = DocumentInfoDto.From(document);
            return known.Contains(document.DocId)
                ? Ok(dto)
                : AcceptedAtAction(nameof(Get), new { docId = document.DocId }, dto);
        }
        catch (InvalidDataException ex)
        {
            logger.LogInformation("Rejected upload {File}: {Reason}", file.FileName, ex.Message);
            return Problem(ex.Message, statusCode: StatusCodes.Status400BadRequest);
        }
    }

    [HttpDelete("{docId}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public IActionResult Delete(string docId) => rag.Remove(docId) switch
    {
        RemoveResult.Removed => NoContent(),
        RemoveResult.NotFound => NotFound(),
        RemoveResult.NotAllowed => Problem("Documents in the source folder can't be deleted through the API.", statusCode: StatusCodes.Status409Conflict),
        _ => Problem("The document is being indexed. Delete it once that finishes.", statusCode: StatusCodes.Status409Conflict)
    };

    [HttpGet("{docId}/chunks/{chunkId}")]
    [ProducesResponseType<ChunkDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<ChunkDto> Chunk(string docId, string chunkId)
    {
        var chunk = rag.Find(docId)?.Index?.Find(chunkId);
        if (chunk is null) return NotFound();

        return Ok(new ChunkDto(chunk.Id, chunk.DocId, chunk.Kind.ToString(), chunk.Part, chunk.PartLabel, chunk.Section,
            chunk.SectionPath, chunk.PdfPageStart, chunk.PdfPageEnd, chunk.PrintedPages, chunk.Unit, chunk.Header, chunk.Text));
    }

    // Linked as /api/documents/{docId}/file#page=25 so the browser's PDF viewer opens on
    // the cited page. Range processing lets it fetch pages without the whole file.
    [HttpGet("{docId}/file")]
    public IActionResult File(string docId)
    {
        if (rag.Find(docId) is not { } document || !System.IO.File.Exists(document.PdfPath))
        {
            return NotFound();
        }

        return PhysicalFile(document.PdfPath, "application/pdf", enableRangeProcessing: true);
    }

    [HttpPost("search")]
    [ProducesResponseType<SearchResultDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<SearchResultDto>> Search([FromBody] SearchRequest request, CancellationToken cancellationToken)
    {
        var (scope, problem) = rag.ResolveScope(request.DocumentIds ?? []);
        if (scope is null)
        {
            return Problem(problem, statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        var r = await scope.Retriever.RetrieveAsync(request.Question, request.PreviousQuestion, cancellationToken);

        return Ok(new SearchResultDto(r.Grounded, r.TopVectorScore, r.ElapsedMilliseconds, r.Query.Vector, r.Query.Keyword,
            r.Query.UsedPreviousTurn, scope.Documents.Select(d => d.DocId).ToArray(),
            r.Sources.Select(s => new SearchHitDto(s.Number, s.Chunk.Id, s.Chunk.DocId, s.Chunk.Section, s.Chunk.PdfPageStart,
                s.Chunk.PdfPageEnd, s.Chunk.PrintedPages, s.VectorScore, s.VectorRank, s.Bm25Rank, s.FusedScore,
                s.Chunk.TokenCount, s.Chunk.Text)).ToArray()));
    }
}
