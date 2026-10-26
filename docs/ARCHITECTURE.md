# Architecture and stack

Diagrams of how the pieces fit together, and the full list of what's used to build it.
Prose reasoning behind the decisions is in [IMPLEMENTATION.md](IMPLEMENTATION.md).

## 1. Deployment

Development runs two processes; production runs one. Nothing leaves the machine at
inference time.

```
 DEVELOPMENT                                      PRODUCTION (npm run build)
 ───────────────────────────────                  ──────────────────────────────

  Browser :4200                                    Browser :5093
      │                                                 │
      │  /api/* proxied by                              │  same origin,
      │  proxy.conf.json                                │  no CORS
      ▼                                                 ▼
  ng serve :4200                                   ┌─────────────────────────┐
      │                                            │  ChatBot.Api :5093      │
      ▼                                            │                         │
  ChatBot.Api :5093  ◄── CORS policy                │  wwwroot/ (Angular)     │
      │                  "angular-dev"             │  /api/*   (controllers) │
      ▼                                            └───────────┬─────────────┘
  llama.cpp (in-process)                                       ▼
      │                                                llama.cpp (in-process)
      ▼                                                        │
  models/*.gguf  (1.04 GB, memory-mapped)                      ▼
                                                        models/*.gguf
```

## 2. Component map

Arrows point the way dependencies run. Nothing above a layer is known to the layer
below it.

```
┌─ chatbot-web (Angular 21) ────────────────────────────────────────────────┐
│                                                                           │
│   app.ts  (shell)                                                         │
│     ├── sidebar/         session list, New chat, delete                   │
│     └── chat-panel/      transcript, autoscroll, context telemetry        │
│           ├── message/   one bubble; markdown via computed()              │
│           └── composer/  textarea, auto-grow, Enter to send, Stop         │
│                                                                           │
│   core/                                                                   │
│     ├── chat-store.ts    ALL state, as signals  ◄── the only stateful bit │
│     ├── chat-api.ts      HttpClient (JSON) + raw fetch (SSE)              │
│     ├── chat.models.ts   hand-written mirror of the C# DTOs               │
│     └── markdown.ts      escape-first renderer, no dependencies           │
└───────────────────────────────┬───────────────────────────────────────────┘
                                │  HTTP + text/event-stream
┌───────────────────────────────▼───────────────────────────────────────────┐
│  Controllers/        SessionsController (CRUD + SSE), ModelController     │
│  Contracts/          Dtos.cs — wire shapes, kept off the domain types     │
├───────────────────────────────────────────────────────────────────────────┤
│  Chat/               ChatService      the order of operations             │
│                      ChatStreamEvent  ContextPlanned | Chunk | Completed  │
├─────────────────────────────┬─────────────────────────────────────────────┤
│  Llm/                       │  Sessions/                                  │
│   IChatTemplate             │   ISessionStore          ◄── storage seam   │
│    └ ChatMlTemplate         │    └ InMemorySessionStore                   │
│   ITokenCounter   ◄── test  │   SessionJanitor  (BackgroundService, 5 min)│
│   ILanguageModel     seam   │                                             │
│    └ LlamaLanguageModel     ├─────────────────────────────────────────────┤
│   ContextWindowPolicy       │  Hosting/                                   │
│   PromptPlan                │   ModelLoaderService (BackgroundService)    │
│   StopSequenceFilter        │                                             │
│   LlamaOptions/ChatOptions  │                                             │
├─────────────────────────────┴─────────────────────────────────────────────┤
│  Domain/             ChatSession  ◄── this object IS the memory           │
│                      ChatMessage (immutable record), ChatRole (enum)      │
└───────────────────────────────────────────────────────────────────────────┘
                                │
                                ▼
                    LLamaSharp ──► llama.cpp ──► *.gguf
```

Two interfaces exist purely as seams and are worth pointing out:

- `ITokenCounter` is split off `ILanguageModel` so `ContextWindowPolicy` can be unit
  tested against `FakeTokenCounter` with no weights loaded.
- `ISessionStore` is where a durable store would go instead of the in-memory one.

## 3. Sending a message

The numbered steps are the whole feature. Step 7 is what makes follow-up questions work.

```
 Browser                    ChatBot.Api                        llama.cpp
 ───────                    ───────────                         ─────────

 composer (Enter)
     │
     │ 1. store.send()
     │    optimistic user bubble appended locally
     │
     │ 2. POST /api/sessions/{id}/messages     [raw fetch, not HttpClient]
     ├──────────────────────────────────────►
     │                              SessionsController
     │                                 │ model.IsReady?  ──► no: 503
     │                                 │ empty / >8000 chars? ──► 400
     │                                 │ set SSE headers
     │                                 ▼
     │                              ChatService
     │                                 │ 3. session.Append(user turn)
     │                                 │
     │                                 │ 4. ContextWindowPolicy.Compose()
     │                                 │      renders system + recent turns + primer
     │                                 │      measures every block with ITokenCounter
     │                                 │      returns PromptPlan
     │  ◄── event: context ────────────┤
     │      {includedMessages, droppedMessages, promptTokens, ...}
     │                                 │
     │                                 │ 5. LlamaLanguageModel.StreamAsync()
     │                                 │      await semaphore (1 at a time)
     │                                 ├────────────────────────────────►
     │                                 │                        StatelessExecutor
     │                                 │                        fresh KV cache
     │                                 │  ◄── "Ret" "rieval" "-aug" ... ─┤
     │                                 │ 6. StopSequenceFilter.Push(chunk)
     │  ◄── event: chunk ──────────────┤      holds back partial "<|im_end|>"
     │      {"text":"Ret"}             │
     │  ◄── event: chunk ──────────────┤
     │      ... repeats ...            │
     │                                 │
     │                                 │ 7. session.Append(assistant turn)
     │                                 │      in a finally block, so a cancelled
     │                                 │      or empty reply still closes the turn
     │  ◄── event: done ───────────────┤
     │      {messageId, completionTokens, elapsedSeconds, tokensPerSecond}
     │
     │ 8. store commits streamingText into messages, clears the caret
     ▼
 repaint (signal write schedules it; the app is zoneless)
```

Error paths all end inside the stream, because the headers are already on the wire by
then: `event: error` with `session_not_found` or `inference_error`. A client abort is not
an error — the server keeps the partial reply and so does the store.

## 4. What the model actually receives

`ContextWindowPolicy` packs newest-first into a fixed budget:

```
  ContextSize 4096
  ├── MaxResponseTokens      512   reserved FIRST, before any history
  ├── SafetyMarginTokens      64   BOS token + tokenizer disagreement
  └── available             3520
        ├── system prompt + primer   (never evicted)
        └── history budget           ◄── recent turns packed in here
```

Rendered, a third turn looks like this. Turn 1 and 2 being present is the entire reason
"the second one" resolves to anything:

```
<|im_start|>system
You are a helpful assistant... use numbered lists when you enumerate...<|im_end|>
<|im_start|>user
What is RAG?<|im_end|>                                     ◄── turn 1
<|im_start|>assistant
Retrieval-augmented generation is...<|im_end|>              ◄── turn 1 reply
<|im_start|>user
What are its main components?<|im_end|>                     ◄── turn 2
<|im_start|>assistant
1. Retriever  2. Index  3. Generator<|im_end|>              ◄── turn 2 reply
<|im_start|>user
Can you explain the second one in detail?<|im_end|>         ◄── turn 3, just arrived
<|im_start|>assistant
                                          ◄── primer; generation starts here
```

Packing rules, and what each one prevents:

```
  walk history newest → oldest
      │
      ├─ fits in remaining budget?  ──► include, subtract, continue
      │
      └─ doesn't fit?
            ├─ is it the newest message?  ──► trim it (keep the tail, mark it),
            │                                 because dropping it would ignore
            │                                 what the user just typed
            └─ otherwise ──► STOP. Do not skip it to find smaller older ones:
                             gaps in a transcript read as non-sequiturs.
```

## 5. Session lifecycle

```
  POST /api/sessions
        │
        ▼
   ┌─────────────┐   Append(user) / Append(assistant)
   │   ACTIVE    │◄──────────────┐
   │ Guid id     │               │  every turn bumps LastActivityAt
   │ messages[]  │───────────────┘
   │ Title       │
   └──────┬──────┘
          │
          ├── DELETE /api/sessions/{id} ────────► removed
          │
          ├── idle > 120 min, SessionJanitor ───► evicted (5-minute sweep)
          │
          └── process restart ──────────────────► gone (in-memory only)
```

Isolation is structural rather than enforced by a check: a new id maps to a new empty
list, and nothing in the system is keyed by anything except that id. There is also no
model-side state that could survive between requests, because `StatelessExecutor`
rebuilds the KV cache every time.

## 6. Technology inventory

### Runtime and toolchain

| Thing | Version | Notes |
|---|---|---|
| .NET SDK | 9.0.101 | `Microsoft.NET.Sdk.Web`, nullable + implicit usings on |
| C# | 13 | primary constructors, collection expressions, `Lock` |
| Node.js | 24.14.0 | build-time only |
| npm | 10.8.2 | pinned via `packageManager` |
| Angular CLI | 21.2 | `@angular/build` esbuild builder |
| TypeScript | 5.9.2 | strict |
| Windows PowerShell | 5.1+ / 7+ | for `scripts/download-model.ps1` |
| curl.exe | ships with Win10+ | resumable multi-GB download |

### Backend packages

| Package | Version | Why it's here |
|---|---|---|
| `LLamaSharp` | 0.27.0 | .NET bindings for llama.cpp — `LLamaWeights`, `StatelessExecutor`, `DefaultSamplingPipeline` |
| `LLamaSharp.Backend.Cpu` | 0.27.0 | native llama.cpp binaries, CPU build (AVX/AVX2/AVX512 variants) |

That's the entire dependency list. Everything else comes from the framework:
`Microsoft.AspNetCore.Mvc` for the controllers, `Microsoft.Extensions.Options` with
`ValidateOnStart` for config, `BackgroundService` for the two hosted services,
`ConcurrentDictionary` and `PeriodicTimer` for sessions, `System.Text.Json` for the SSE
payloads.

### Test packages

| Package | Version | Role |
|---|---|---|
| `xunit` | 2.9.2 | 15 tests |
| `xunit.runner.visualstudio` | 2.8.2 | test discovery |
| `Microsoft.NET.Test.Sdk` | 17.11.1 | `dotnet test` host |
| `Microsoft.Extensions.Options` | 9.0.0 | `Options.Create` in test setup |
| `coverlet.collector` | 6.0.2 | coverage collection |

### Frontend packages

| Package | Version | Role |
|---|---|---|
| `@angular/core` | 21.2.23 | standalone components, signals, zoneless |
| `@angular/common` | 21.2 | `HttpClient`, `DatePipe`, `DecimalPipe` |
| `@angular/compiler`, `@angular/platform-browser` | 21.2 | runtime |
| `@angular/forms`, `@angular/router` | 21.2 | present from the CLI scaffold; barely used |
| `rxjs` | 7.8 | only `firstValueFrom` to await `HttpClient` |
| `tslib` | 2.3 | TS helpers |
| `prettier` | 3.8.1 | formatting |

No UI library, no state library, no markdown library, no SSE library. Styling is
hand-written SCSS (`styles.scss` plus one `.scss` per component); markdown is
`core/markdown.ts`; SSE is parsed by hand in `core/chat-api.ts`. Production bundle is
210 kB raw / 61 kB transferred.

### Model

| Property | Value |
|---|---|
| Model | Qwen2.5-1.5B-Instruct |
| Format | GGUF v3, Q4_K_M quantisation |
| On disk | 1.04 GB |
| Parameters | 1,777,088,000 |
| Layers | 28 |
| Native context | 32768 (we configure 4096) |
| Tokenizer | GPT-2 BPE, ChatML special tokens |
| Prompt format | ChatML |
| Licence | Apache 2.0 |
| Source | `huggingface.co/Qwen/Qwen2.5-1.5B-Instruct-GGUF` |
| Load time | ~8s cold, CPU-only |
| Throughput | ~3-7 tokens/sec on an i5-10210U |

`scripts/download-model.ps1` also offers `qwen2.5-0.5b` (~0.4 GB, weaker at follow-ups)
and `qwen2.5-3b` (~2.0 GB, better answers, roughly half the speed). Switching means
updating `Llama:ModelPath` to match. Any ChatML-family GGUF works without code changes;
a different prompt format needs one new `IChatTemplate`.

### Protocols and browser APIs

| Thing | Where |
|---|---|
| Server-Sent Events | `SessionsController.SendEventAsync`, parsed in `chat-api.ts` |
| `fetch` + `ReadableStream` | streaming reader — `HttpClient` buffers, so it can't be used here |
| `TextDecoder({stream:true})` | multi-byte UTF-8 split across network chunks |
| `AbortController` | the Stop button; server keeps the partial reply |
| Async generators | `chat-api.ts` yields one typed event per SSE record |
| `X-Accel-Buffering: no` | stops a reverse proxy re-buffering the stream |
