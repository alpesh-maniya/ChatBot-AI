# ChatBot

A ChatGPT-style chat app with session-based conversation memory. The LLM runs inside the
ASP.NET Core process, so there are no API keys and no network calls at inference time.

- **Backend** — ASP.NET Core 9, LLamaSharp 0.27 (llama.cpp bindings)
- **Frontend** — Angular 21, standalone components, zoneless, no UI library
- **Model** — Qwen2.5-1.5B-Instruct, Q4_K_M (~1.0 GB)

`docs/ARCHITECTURE.md` has the diagrams and the full dependency list.
`docs/IMPLEMENTATION.md` covers how it works and why — read that before changing
anything under `Llm/`.

## Requirements

.NET 9 SDK, Node 20+, and ~1.5 GB free for the model and build output. No GPU needed;
the default config is CPU-only.

## Getting it running

```powershell
# 1. Fetch the weights (~1.0 GB, resumable)
./scripts/download-model.ps1

# 2. Start the API  (http://localhost:5093)
dotnet run --project backend/ChatBot.Api

# 3. In a second terminal, start the UI  (http://localhost:4200)
cd chatbot-web
npm install
npm start
```

Open http://localhost:4200. The composer is disabled for the first few seconds while the
weights are mapped; the sidebar shows model status while that happens.

### Single process

`ng build` writes the Angular bundle into `backend/ChatBot.Api/wwwroot`, which the API
serves as static files:

```powershell
cd chatbot-web; npm run build; cd ..
dotnet run --project backend/ChatBot.Api
```

Everything is then on http://localhost:5093, same origin, no CORS involved.

## Tests

```powershell
dotnet test backend/ChatBot.sln
```

15 tests over the context-window packing rules and the stop-sequence filter. They use a
fake tokenizer, so they need no model file and run in under a second.

## Layout

```
backend/ChatBot.Api/
  Domain/      ChatMessage, ChatSession - the conversation itself
  Sessions/    storage seam, in-memory implementation, idle eviction
  Llm/         chat template, context-window policy, LLamaSharp wrapper
  Chat/        order of operations for answering a message
  Controllers/ HTTP surface including the SSE stream
chatbot-web/src/app/
  core/        API client, signal store, markdown renderer
  components/  sidebar, chat panel, message bubble, composer
docs/ARCHITECTURE.md    diagrams, technology and package inventory
docs/IMPLEMENTATION.md  how each requirement is built, and why
scripts/download-model.ps1
```

## API

| Method | Route | Purpose |
|---|---|---|
| `POST` | `/api/sessions` | Start a conversation, returns its id |
| `GET` | `/api/sessions` | List conversations |
| `GET` | `/api/sessions/{id}` | Full transcript |
| `DELETE` | `/api/sessions/{id}` | Discard a conversation |
| `POST` | `/api/sessions/{id}/messages` | Send a message, streams the reply as SSE |
| `GET` | `/api/model/status` | Whether the weights are loaded |

## Using a different model

`Llama:ModelPath` in `appsettings.json` takes any GGUF. The 1.5B default was chosen for
a 4-core laptop CPU with no usable GPU offload; it runs at roughly 3-7 tokens/sec. A 3B
or 7B gives noticeably better answers at 2-3x the latency. If the new model isn't a
ChatML family (Qwen, Yi, Hermes), you also need an `IChatTemplate` implementation for its
prompt format.

## Known limitations

Conversations live in process memory and are lost on restart. There's no authentication,
so anyone who can reach the API can read any session id they can guess. Inference is
serialised, one reply at a time. All three are deliberate for a single-node demo and
covered in `docs/IMPLEMENTATION.md`.
