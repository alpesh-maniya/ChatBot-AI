using ChatBot.Api.Contracts;
using ChatBot.Api.Llm;
using Microsoft.AspNetCore.Mvc;

namespace ChatBot.Api.Controllers;

// Lets the UI tell "still loading" apart from "backend is broken". The client polls this
// on startup and keeps the composer disabled until Ready.
[ApiController]
[Route("api/model")]
public sealed class ModelController(ILanguageModel model) : ControllerBase
{
    [HttpGet("status")]
    [ProducesResponseType<ModelStatusDto>(StatusCodes.Status200OK)]
    public ActionResult<ModelStatusDto> Status()
    {
        var info = model.Info;
        return Ok(new ModelStatusDto(info.Name, info.Ready, info.ContextSize, info.ParameterCount, info.LoadError));
    }
}
