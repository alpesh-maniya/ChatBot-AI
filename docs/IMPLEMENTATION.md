# How this is built

Notes on the implementation: what each requirement maps to in the code, and the
decisions that weren't obvious at the time.

Stack is ASP.NET Core 9 for the API, Angular 21 for the UI, and Qwen2.5-1.5B-Instruct
running in-process through LLamaSharp. No API keys and no network calls at inference
time.

For diagrams and the full package inventory, see [ARCHITECTURE.md](ARCHITECTURE.md).

## Contents

1. [Running it](#running-it)
2. [One message, end to end](#one-message-end-to-end)
3. [Loading the model](#loading-the-model)
4. [The prompt template](#the-prompt-template)
5. [Session history](#session-history)
6. [Fitting history into the context window](#fitting-history-into-the-context-window)
7. [Follow-up questions and topic changes](#follow-up-questions-and-topic-changes)
8. [Streaming to the browser](#streaming-to-the-browser)
9. [The UI](#the-ui)
10. [Config reference](#config-reference)
11. [Tests](#tests)
12. [What it doesn't do](#what-it-doesnt-do)

## Running it

```powershell
./scripts/download-model.ps1                   # ~1.0 GB, resumable
dotnet run --project backend/ChatBot.Api       # http://localhost:5093

cd chatbot-web && npm install && npm start     # http://localhost:4200
```

The composer stays disabled for the first few seconds while the weights are mapped. The
sidebar shows model status while that happens.

`ng build` writes straight into `backend/ChatBot.Api/wwwroot`, so for a single-process
run do `npm run build` and then just start the API. Everything is on port 5093 then,
same origin, and CORS doesn't come into it.

## One message, end to end

```
POST /api/sessions/{id}/messages
  │
  ├─ SessionsController        validates, sets SSE headers
  ├─ ChatService               appends the user turn to the session
  ├─ ContextWindowPolicy       renders system + recent turns + primer, measures tokens
  ├─ LlamaLanguageModel        StatelessExecutor.InferAsync, token by token
  │    └─ StopSequenceFilter   holds back anything that might be "<|im_end|>"
  ├─ ChatService               appends the assistant turn to the session
  └─ SessionsController        writes context / chunk / done events
```

The ordering in `ChatService` is the part that matters. The user turn goes into the
session *before* the policy runs, and the assistant turn goes in after generation
finishes. That second append is what makes the next question resolvable: nothing else in
the system remembers the conversation.

## Loading the model

Qwen2.5-1.5B-Instruct at Q4_K_M, about 1.0 GB on disk, 1.78B parameters. It was picked
for the machine rather than for quality: an i5-10210U with no usable GPU offload. It
runs at roughly 3-7 tokens/sec here, which is slow but usable. `Llama:ModelPath` takes
any GGUF, so swapping in a 3B or 7B is a config change plus a download.

`ModelLoaderService` is a `BackgroundService` that maps the weights after the host
starts. Doing it inline in `Program.cs` would leave the app unreachable for those
seconds, which from the browser looks identical to a crash. This way
`GET /api/model/status` answers honestly the whole time and the client polls it.

A failed load doesn't kill the process. The API stays up and reports the reason through
that endpoint, which is easier to debug than an exit before the first log line.

`LlamaLanguageModel` is registered as a singleton, since the weights are the expensive
thing. Two details in it are easy to get wrong:

- **`StatelessExecutor`, not `InteractiveExecutor`.** A fresh KV cache per request, so
  the prompt is reprocessed every turn and cost is O(conversation length). The upside is
  that one session cannot see another's context, because there's no retained state that
  could leak. Enforcing that structurally beats remembering to reset. It's the wrong
  trade once conversations get long, and `ILanguageModel` is where to revisit it.
- **`ApplyTemplate = false`.** We render ChatML ourselves. Leaving this on wraps the
  prompt in the GGUF's built-in template a second time, and the model starts answering
  slightly oddly rather than failing.

Inference is serialised behind a `SemaphoreSlim(1,1)`. Two concurrent requests on four
cores don't finish sooner, they each run at half speed and double peak RAM. The
semaphore makes that queue explicit instead of letting it form in the thread pool.

## The prompt template

Qwen2.5-Instruct was fine-tuned on ChatML, so that's what `ChatMlTemplate` emits:

```
<|im_start|>system
You are a helpful assistant...<|im_end|>
<|im_start|>user
What is RAG?<|im_end|>
<|im_start|>assistant
Retrieval-augmented generation is...<|im_end|>
<|im_start|>user
What are its main components?<|im_end|>
<|im_start|>assistant
```

That trailing `<|im_start|>assistant\n` with nothing after it is the generation primer.
Leave it off and the model continues writing the *user's* turn instead of answering.

`IChatTemplate` is an interface because the format belongs to the model, not to us.
Qwen, Yi and Hermes are all ChatML; Llama-3 uses different header tokens. Changing model
family means one new implementation and nothing else.

Token counting passes `special: true` to `Tokenize`. Without it `<|im_start|>` counts as
the eight-or-so tokens of its literal text instead of the single special token it is,
and every context calculation comes out high. This was worth an hour to find.

The system prompt lives in `appsettings.json` rather than in a const, because prompt
wording needs a lot of iteration. The current one tells the model to resolve references
against the history, to follow the user if they change subject, and to use numbered lists
when enumerating so that "the second one" has something to point at.

## Session history

`ChatSession` is the conversation. There's no model-side state anywhere, so this object
is the entire memory.

- `Append` is the only mutator. `ChatMessage` is an immutable record, so a turn is never
  edited after the fact and the history stays an accurate record of what the model was
  shown.
- `Snapshot()` returns a copy. Callers iterate it while inference runs for many seconds,
  and a concurrent append would invalidate a live enumerator.
- `Title` is the first user message, trimmed to 48 characters, used as the sidebar label.

`InMemorySessionStore` is a `ConcurrentDictionary<Guid, ChatSession>` behind
`ISessionStore`. History dies with the process. That's a real limitation and the
interface is where a durable store goes instead.

`SessionJanitor` sweeps every five minutes and drops sessions idle past
`Chat:SessionIdleTimeoutMinutes` (default 120). Without it, an in-memory store is an
unbounded leak: every browser that ever opened the app keeps a conversation alive.

**Fresh sessions don't inherit anything.** `POST /api/sessions` creates a new GUID
mapped to an empty message list, and nothing in the system is keyed by anything except
that id. On the client, "New chat" calls that endpoint and clears its local signals.
There's no path by which the previous conversation reaches the next prompt. Verified by
asking the same follow-up in a new session:

```
session A, turn 2:  "What is the second one?"  -> "The second one is Blue."   [3 msgs in context]
session B, turn 1:  "What is the second one?"  -> asks what you're referring to [1 msg in context]
```

## Fitting history into the context window

The window is 4096 tokens and fixed. A conversation isn't. So on every turn something
has to decide what fits, and `ContextWindowPolicy` is that decision written down instead
of left to whatever llama.cpp does on overflow, which is to produce garbage rather than
throw.

```
[ system ][ ...as many recent turns as fit... ][ primer ][ response budget ]
|<-------------------------- 4096 tokens ------------------------------->|
```

Budget is `ContextSize - MaxResponseTokens - SafetyMarginTokens`, so 4096 - 512 - 64.
The reply's space comes off the top before any history is packed. Skip that and a long
conversation eventually squeezes the answer, which shows up as replies that stop
mid-sentence and reads like a broken model.

Three rules, each because the obvious alternative is worse:

1. The system prompt is reserved first and never evicted. Packed like an ordinary
   message it would be the oldest thing in the prompt and therefore first to go, so the
   bot would lose its instructions exactly when the conversation got long enough to need
   them.
2. Packing walks newest to oldest and stops at the first message that doesn't fit. It
   does not skip that one to look for smaller older ones. Skipping leaves gaps, and a
   transcript with gaps reads to the model as a run of non-sequiturs. A short contiguous
   history beats a long perforated one.
3. Costs are measured, not estimated. Each message is rendered to its final string and
   tokenised. The delimiters are real tokens and on a short turn they're a good fraction
   of the total, so character-count heuristics drift in exactly the direction that
   overflows.

One special case: if the newest message alone overflows the budget, dropping it would
mean ignoring what the user just typed. `FitToBudget` trims it instead, keeping the tail
(in a long paste the actual question is usually at the end) behind a visible marker. The
trim is a binary search on character length rather than a token slice, because getting a
string back out of a truncated token sequence needs a detokenise round trip that isn't
reliably lossless.

The policy returns a `PromptPlan`, and the interesting fields go to the client as the
`context` SSE event rather than only to the log. "Why did it forget that?" is the usual
complaint about a chatbot and the answer is normally sitting in `DroppedMessages`. The UI
prints it under the composer.

If the system prompt plus the reserved reply leave no room for history at all, `Compose`
throws with the numbers in the message. That's a config error, and failing on the first
request beats silently serving a bot with no memory.

## Follow-up questions and topic changes

There's no special machinery for this. Follow-ups work because the whole recent
transcript is in the prompt and the model resolves the reference itself. What the code
contributes is making sure the relevant turns are actually *there*: the append ordering
in `ChatService`, and newest-first packing in the policy.

The three-turn case from the brief, run against the real model, with the context event
included:

```
> What is RAG?
  [1 msg, 169 prompt tokens]

> What are its main components?
  [3 msgs, 294 prompt tokens]   <- prior Q and A are both in the prompt

> Can you explain the second one in detail?
  [5 msgs, 530 prompt tokens]   <- resolves "the second one" against turn 2's list
```

The token count climbing 169 → 294 → 530 is the mechanism working: each turn carries
everything before it.

Worth being straight about one thing. The *mechanism* is solid, but Qwen2.5-1.5B doesn't
reliably know what RAG is — on the run above it glossed it as "Reinforcement Learning
Agent" and the rest of the conversation built on that wrong premise, coherently. That's
model capability, not context handling; the same three turns about a topic it knows
resolve correctly. A 3B or 7B fixes the knowledge gap at 2-3x the latency on this
hardware. Point `Llama:ModelPath` at a bigger GGUF if the demo needs it.

Topic changes need no handling either, and the system prompt explicitly tells the model
not to force a connection back to the earlier subject. Older turns stay in the window
until they age out of the budget, which is what you want: the user may come back to them.

## Streaming to the browser

A 200-token answer takes 15-20 seconds here, so waiting for a whole JSON response feels
broken. The reply streams as Server-Sent Events: one `context` event, then `chunk`
events, then `done` (or `error`).

SSE rather than WebSockets because the traffic is one-directional and short-lived; a
duplex channel plus its lifecycle would be machinery with nothing to do. The response is
written straight to `Response.Body` with a flush per event, because the framework result
types buffer and buffering is the one thing being avoided. `X-Accel-Buffering: no` stops
a reverse proxy from undoing that.

Chunk text is JSON-encoded rather than written raw. SSE ends a record at a blank line,
and any markdown answer contains one, so raw text would terminate the event early and
corrupt the stream.

`StopSequenceFilter` is the least obvious piece. `<|im_end|>` comes out of the decoder
split across chunks — `"<|im"`, `"_end"`, `"|>"` — and llama.cpp only halts once it
recognises the whole thing, by which point the fragments are already rendered in the
browser. So the filter never emits trailing text that could still become a stop
sequence. At most `longest stop sequence - 1` characters are withheld, which is under a
token of latency, and `Flush()` releases them when the model stops on its own.

`ChatService` closes the turn with an assistant message however generation ended:
cancelled, empty, or thrown. Partial answers are worth keeping, and more importantly the
transcript has to stay strictly user/assistant alternating. Two user turns in a row is a
prompt shape the model never saw in training and it answers noticeably worse.

## The UI

Angular 21, standalone components, zoneless, no component library.

`ChatStore` holds all state as signals. Zoneless is the default in 21, and under it a
signal write is what schedules rendering; an RxJS subject written to from a `fetch`
callback would update nothing without an explicit `markForCheck`.

The in-flight reply sits in its own `streamingText` signal rather than in `messages`. It
isn't a message yet: no server id, and it might be cancelled. Keeping it separate means
every append to `messages` is final.

`ChatApi` uses `HttpClient` for the ordinary JSON calls and raw `fetch` for the streaming
one. `HttpClient` buffers the entire body before it emits, which defeats the point;
`fetch` exposes a `ReadableStream`. `EventSource` would have been the obvious choice but
it's GET-only and the message has to go in a body. The `TextDecoder` is called with
`{ stream: true }` because a multi-byte UTF-8 character can straddle two network chunks.

Markdown goes through a ~120-line renderer in `core/markdown.ts` instead of marked or
markdown-it. Those hand back HTML that still needs sanitising before `[innerHTML]`, and
model output is untrusted. Here every HTML-significant character is escaped *first* and
the transforms run over already-escaped text, so no input can produce a tag we didn't
generate. The result is still bound with plain `[innerHTML]` rather than
`bypassSecurityTrustHtml`, so Angular's sanitiser gets a second pass at it.

Autoscroll sticks to the bottom unless the user has scrolled up more than 48px.
Scrolling unconditionally yanks the view down on every token, which makes it impossible
to read back through a long answer while it's still being written.

Components under `components/` are presentational and take inputs / emit outputs.
`ChatPanelComponent` is the only one that touches the store.

## Config reference

`appsettings.json`, validated at startup with `ValidateOnStart` so a bad combination
fails immediately rather than on the first chat request.

| Key | Default | Notes |
|---|---|---|
| `Llama:ModelPath` | `../../models/qwen2.5-1.5b-instruct-q4_k_m.gguf` | Relative paths resolve against the content root. |
| `Llama:ContextSize` | 4096 | KV cache size and the hard ceiling on prompt + reply. Costs RAM roughly linearly. Qwen2.5 supports 32k if you want it. |
| `Llama:MaxResponseTokens` | 512 | Reserved before history is packed. |
| `Llama:SafetyMarginTokens` | 64 | Slack for the BOS token and tokenizer disagreement. |
| `Llama:Threads` | null | Defaults to physical cores. Hyper-threaded siblings share vector units, so going past core count measured slower here. |
| `Llama:GpuLayerCount` | 0 | CPU only. |
| `Llama:Temperature` / `TopP` / `TopK` / `RepeatPenalty` | 0.7 / 0.9 / 40 / 1.1 | Ordinary chat sampling. |
| `Llama:Seed` | null | Set an integer for reproducible replies while debugging. |
| `Chat:SystemPrompt` | see file | Kept in config because prompt wording needs iteration. |
| `Chat:SessionIdleTimeoutMinutes` | 120 | Used by `SessionJanitor`. |
| `Chat:MaxUserMessageCharacters` | 8000 | Rejected at the controller, before the tokenizer. |

## Tests

`dotnet test backend/ChatBot.sln` — 15 tests, under a second, no model file needed.

They cover the two pieces with non-obvious rules and no I/O: `ContextWindowPolicy` and
`StopSequenceFilter`. The policy is testable without a gigabyte of weights because token
counting sits behind `ITokenCounter` and the suite substitutes `FakeTokenCounter`
(one token per four characters, deliberately inaccurate — the tests are about packing
decisions, not tokenisation).

Not tested: the LLamaSharp wrapper, which would be testing the library, and generation
quality, which is non-deterministic by default and produces tests that fail for reasons
unrelated to the code.

## What it doesn't do

- **History is lost on restart.** In-memory only. `ISessionStore` is the seam.
- **No auth.** Anyone who can reach the API can read any session id they can guess.
  GUIDs are obscurity, not authorisation.
- **One reply at a time.** Inference is serialised, so a second user waits.
- **No retrieval, no tools, no function calling.** Just chat over the transcript.

If this needed to go further, roughly in the order the ceilings would be hit: a
per-session executor so a turn costs O(new tokens) instead of O(conversation); a durable
session store; GPU offload via `GpuLayerCount`; and summarising eviction instead of
dropping old turns, for which `PromptPlan.DroppedMessages` going non-zero is the signal
that it would start paying off.
