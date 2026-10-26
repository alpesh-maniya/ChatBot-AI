using ChatBot.Api.Chat;
using ChatBot.Api.Hosting;
using ChatBot.Api.Llm;
using ChatBot.Api.Rag;
using ChatBot.Api.Sessions;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOptions<LlamaOptions>()
    .Bind(builder.Configuration.GetSection(LlamaOptions.SectionName))
    .Validate(o => !string.IsNullOrWhiteSpace(o.ModelPath), "Llama:ModelPath must be set.")
    .Validate(o => o.MaxResponseTokens > 0 && o.MaxResponseTokens < o.ContextSize,
        "Llama:MaxResponseTokens must be greater than zero and smaller than Llama:ContextSize.")
    .ValidateOnStart();

builder.Services.AddOptions<ChatOptions>()
    .Bind(builder.Configuration.GetSection(ChatOptions.SectionName))
    .Validate(o => !string.IsNullOrWhiteSpace(o.SystemPrompt), "Chat:SystemPrompt must be set.")
    .ValidateOnStart();

// Resolve a relative model path against the content root so it behaves the same from an
// IDE, from `dotnet run`, and from a published folder.
builder.Services.PostConfigure<LlamaOptions>(o =>
{
    if (!Path.IsPathRooted(o.ModelPath))
    {
        o.ModelPath = Path.GetFullPath(Path.Combine(builder.Environment.ContentRootPath, o.ModelPath));
    }
});

builder.Services.AddOptions<RagOptions>()
    .Bind(builder.Configuration.GetSection(RagOptions.SectionName))
    .Validate(o => !o.Enabled || (!string.IsNullOrWhiteSpace(o.SourceFolder) && !string.IsNullOrWhiteSpace(o.UploadFolder)
                                  && !string.IsNullOrWhiteSpace(o.IndexFolder)),
        "Rag:SourceFolder, Rag:UploadFolder and Rag:IndexFolder must be set when Rag:Enabled is true.")
    .Validate(o => o.MaxUploadMegabytes is > 0 and <= 200, "Rag:MaxUploadMegabytes must be between 1 and 200.")
    .Validate(o => !o.Enabled || !string.IsNullOrWhiteSpace(o.Embedding.ModelPath), "Rag:Embedding:ModelPath must be set when Rag:Enabled is true.")
    .Validate(o => !o.Enabled || !string.IsNullOrWhiteSpace(o.SystemPrompt), "Rag:SystemPrompt must be set when Rag:Enabled is true.")
    .Validate(o => o.Retrieval.MaxSources > 0 && o.Retrieval.SourceTokenBudget > 0, "Rag:Retrieval needs MaxSources and SourceTokenBudget above zero.")
    .ValidateOnStart();

// Same rule as the model path: relative paths are relative to the content root.
builder.Services.PostConfigure<RagOptions>(o =>
{
    string Resolve(string path) => string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path)
        ? path
        : Path.GetFullPath(Path.Combine(builder.Environment.ContentRootPath, path));

    o.SourceFolder = Resolve(o.SourceFolder);
    o.UploadFolder = Resolve(o.UploadFolder);
    o.IndexFolder = Resolve(o.IndexFolder);
    o.Embedding.ModelPath = Resolve(o.Embedding.ModelPath);
});

builder.Services.AddSingleton<RagRuntime>();

builder.Services.AddSingleton<IChatTemplate, ChatMlTemplate>();
builder.Services.AddSingleton<ContextWindowPolicy>();
builder.Services.AddSingleton<ISessionStore, InMemorySessionStore>();

// One instance per process - the weights are the expensive thing here.
builder.Services.AddSingleton<LlamaLanguageModel>();
builder.Services.AddSingleton<ILanguageModel>(sp => sp.GetRequiredService<LlamaLanguageModel>());

builder.Services.AddScoped<ChatService>();

builder.Services.AddHostedService<ModelLoaderService>();
builder.Services.AddHostedService<RagLoaderService>();
builder.Services.AddHostedService<SessionJanitor>();

builder.Services.AddControllers();
builder.Services.AddProblemDetails();

const string DevCors = "angular-dev";
builder.Services.AddCors(options =>
{
    // Dev only. In production this host serves the Angular bundle, so requests are
    // same-origin and CORS doesn't come into it.
    options.AddPolicy(DevCors, policy => policy
        .WithOrigins("http://localhost:4200")
        .AllowAnyHeader()
        .AllowAnyMethod());
});

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseCors(DevCors);
}

app.UseExceptionHandler();
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapControllers();

// Any non-API path that isn't a real file belongs to the Angular router. Without this,
// refreshing the page 404s.
app.MapFallbackToFile("index.html");

app.Run();
