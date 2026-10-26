using ChatBot.Api.Chat;
using ChatBot.Api.Hosting;
using ChatBot.Api.Llm;
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

builder.Services.AddSingleton<IChatTemplate, ChatMlTemplate>();
builder.Services.AddSingleton<ContextWindowPolicy>();
builder.Services.AddSingleton<ISessionStore, InMemorySessionStore>();

// One instance per process - the weights are the expensive thing here.
builder.Services.AddSingleton<LlamaLanguageModel>();
builder.Services.AddSingleton<ILanguageModel>(sp => sp.GetRequiredService<LlamaLanguageModel>());

builder.Services.AddScoped<ChatService>();

builder.Services.AddHostedService<ModelLoaderService>();
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
