# ChatBot

A ChatGPT-style chat app with session-based conversation memory, answering questions about
PDFs with retrieval-augmented generation (RAG) and page citations. Three reports ship with
the app, one per use case: the GPO 2018 Annual Report (plain text), the World Bank's Global
Economic Prospects, June 2026 (200 pages, chapters and regions) and Apple's iMac Product
Environmental Report (tables, a chart, endnotes). Any other PDF can be attached to a
conversation from the UI.
The LLM and the embedding model both run inside the ASP.NET Core process, so there are no
API keys and no network calls at inference time.

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
# 1. Fetch the weights (~1.0 GB chat model + ~36 MB embedding model, resumable)
./scripts/download-model.ps1
./scripts/download-model.ps1 -Model bge-small

# 2. (Optional) build the report index now (~55 s). The API does this itself on first
#    start for any PDF in data/source without a current index; this just lets you read
#    the --dump output first.
dotnet run --project backend/ChatBot.Ingest -- --dump

# 3. Start the API  (http://localhost:5093)
dotnet run --project backend/ChatBot.Api

# 4. In a second terminal, start the UI  (http://localhost:4200)
cd chatbot-web
npm install
npm start
```

`--dump` also writes `blocks.txt` and `chunks.txt` next to the index - read those to see
what the parser made of the PDF. `--all` ingests every PDF in `data/source`, `--input x.pdf`
a single other file. Set `Rag:Enabled` to `false` for the plain chatbot.

## Documents

Every PDF the API finds is a document in the library:

- `data/source/*.pdf` ship with the app. A `<name>.profile.json` beside one (or a profile
  whose `sourceFile` names it) gives it hand-tuned page rules, a title, the system-prompt
  subject and example questions, as `gpo-ar-fy2018.profile.json` does for the report.
- Uploads (`POST /api/documents`) go to `data/uploads`. They get default rules: one part,
  page numbers from the footer, clause numbers kept with their text, all-caps numbered
  lines as headings, a title from the PDF metadata or the file name. Uploading a file
  that is already in the library returns that document. The web UI doesn't upload or show
  them; it works with the three reports in `data/source` only.

At startup a document whose index in `data/index/<docId>` matches its PDF (file hash,
embedding model, chunker version) loads in milliseconds; anything else is queued, and a
background worker indexes the queue one document at a time. A 7-page PDF takes ~3 s, the
82-page report ~55 s. Status moves Queued, Processing ("Embedding 40/154"), Ready or Failed.

Each conversation picks what it answers from with the General chat | Reports switch at
the top right (the Reports side opens the list of reports), and the server keeps that
choice with the session, so switching chats brings each one's back:

- **Reports** - any mix of the three, all of them for a new chat. Answers cite the page.
  With more than one, the question goes to the reports whose best passage scores within
  `Rag:Retrieval:DocumentMargin` (0.05) of the best report's; the rest sit it out, so a
  GPO question is searched exactly as it would be with GPO alone. Without the margin the
  other reports' keyword matches ("employ" in the World Bank report) took two thirds of
  the keyword slots and pushed GPO's own passages down.
- **General chat** - the model on its own, no document search, nothing cited.

A new chat starts on the same choice as the one open. Greetings and thanks skip retrieval
either way. Each passage is labelled with its report for the model, and each report has
a colour on the citation chips. Scanned PDFs without a text layer fail with a message
saying so - there is no OCR.

### Long, structured reports

`GEP-Jun-2026.profile.json` (the World Bank's 200-page Global Economic Prospects) uses the
profile settings for documents whose structure the default rules can't see:

| Setting | What it does |
|---|---|
| `parts[].section` | The chapter title as the top of every section path, when it is only on a divider page |
| `headingStyles` | Font + size range (+ optional regex) = heading level. Catches sub-headings set at body size in another face |
| `dropStyles` | Fonts of chart furniture (axis ticks, panel letters, source lines) to leave out |
| `skipPages` | Contents pages and reference lists, which match any question's keywords and answer none |
| `glossaryPages` | An abbreviations page, read into the acronym map ("SAR" also searches "South Asia") |
| `routes` | Names a question can use for a section ("South Asia", "SAR"); search is limited to it. "Chapter N", "Box N.N" and "Annex N.N" are matched automatically once a profile has routes |
| `sectionLead` | When the best match and another share a section, the section's opening paragraph becomes passage [1] |

Every chunk's header carries its path ("Chapter 2: Regional Outlooks > South Asia >
Outlook"), and the header is part of what is embedded and keyword-indexed.

### Designed documents

`imac-per-oct2024.profile.json` (Apple's 12-page iMac report) is a brochure: facts sit in
call-outs, small tables, a bar chart and numbered endnotes rather than paragraphs.

| Setting | What it does |
|---|---|
| `tables` | A page region that is one table. Rows are rebuilt from word positions, every value next to its label ("Product use \| 45%"), one chunk per table |
| `figures` | A page region that is one chart. The text layer only - its value labels ("445 kg Baseline emissions", "-99 kg Emissions reductions") kept together as one chunk |
| `endnotePages` | Superscript markers ("ENERGY 18 STAR") come out of the sentence, and the note's text is placed right after the sentence that cites it |
| `joinCallouts` | A big figure and its caption below ("76%" / "recycled content in fiber packaging") become one paragraph |
| `headingStyles` level 0 | A large bold style that is a statement, not a title |

There is no vision model: what a chart shows only in its bars is not indexed. Regions are
in PDF points from the bottom-left corner, as `--fonts --pages` prints them.

A profile edit is not detected as a reason to re-index (the check is the PDF hash, the
embedding model and the chunker version). `routes`, `sectionLead`, the title, subject and
example questions apply at the next API start; anything that changes chunks needs
`dotnet run --project backend/ChatBot.Ingest -- --input <pdf>`, or deleting the
document's `data/index/<docId>` folder.

To write a profile for a new report:

```powershell
dotnet run --project backend/ChatBot.Ingest -- --input x.pdf --fonts               # fonts, sizes, examples
dotnet run --project backend/ChatBot.Ingest -- --input x.pdf --fonts --pages 19,96 # every block on two pages
dotnet run --project backend/ChatBot.Ingest -- --input x.pdf --parse-only          # blocks.txt / chunks.txt in seconds
dotnet run --project backend/ChatBot.Ingest -- --input x.pdf --no-profile --output tmp/base   # default rules, for comparison
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

112 tests over context-window packing (with and without sources), the stop-sequence
filter, and the RAG pieces: text cleaning, table stitching, numbered clauses, chunking,
BM25, rank fusion, multi-document retrieval and the document margin, prompt templates,
query building and follow-up detection, citation checks, heading styles, section
routing, section lead, the prompt budget, note markers, table regions and call-outs. They use fake tokenizers, so they need no model file and run
in under a second.

### Evaluation

`data/eval/` holds the official sample questions for each use case (`*.official.json`)
and wider sets (`*.questions.json`) with gold pages and the facts an answer must contain.

```powershell
# retrieval only, seconds; --sources lists every passage with its scores
dotnet run --project backend/ChatBot.Eval -- --retrieval-only --passage-max-chars 700 --questions data/eval/gep-jun-2026.questions.json --doc wb-gep-jun-2026
# full answers through the running API, minutes
dotnet run --project backend/ChatBot.Eval -- --questions data/eval/gep-jun-2026.questions.json --doc wb-gep-jun-2026
```

`--no-routes` and `--no-section-lead` switch a profile technique off for one run.

Each question normally gets a fresh session. `--one-session` asks them all in one
conversation, the way a user would, and `--docs` attaches several documents at once
(one question file per document, same order) and fails any answer citing a passage from
the wrong one:

```powershell
dotnet run --project backend/ChatBot.Eval -- --one-session `
  --questions data/eval/gpo-ar-fy2018.questions.json,data/eval/gep-jun-2026.questions.json,data/eval/imac-per-oct2024.questions.json `
  --docs gpo-ar-fy2018,wb-gep-jun-2026,apple-imac-per-oct2024
```

A question only counts as a follow-up, searched together with the one before it and given
the earlier turns, when it doesn't name its own subject: fewer than four content words
before its first "it", "that", "this" or "compared", or a short "And in FY 2017?". Any
other question goes to the model with its sources and nothing else. Before this rule,
"what percentage of it is recycled?" was joined to whatever came before, and one answer
gave the iMac's emission cut as "20.6%", the idle wattage from four questions earlier.

Results so far, on the 1.5B model. "Default rules" is the same PDF ingested with
`--no-profile`; "found" means a gold page was among the passages sent.

| Set | Default rules | With profile |
|---|---|---|
| Official 9 (3 per report), answers | 6/9 | 9/9 |
| World Bank wider set, found / MRR | 6/12, 0.375 | 12/12, 0.896 |
| World Bank wider set, answers | 8/14 | 12/14 |
| iMac wider set, MRR | 0.95 | 1.0 |
| iMac wider set, answers | 7/12 | 11/12 |

For the World Bank report the section lead does most of the work: without it the profile
finds 10/12 at MRR 0.597, while switching routes off changes nothing on this set, because
the heading path in each chunk already keeps the regions apart. The iMac report was
already easy to find; what the profile fixes is what the model reads. Table rows keep
their column labels, so "Production 50%" stays next to its number, and footnote text
sits next to the sentence that cites it.

The answers still wrong are the model, not retrieval: it read 4.2 percent and wrote 4.3
(East Asia), read the Sleep row instead of Idle in the power table, and once said "not
found" with the answer in passage [2]. Both off-topic questions per report are declined,
but by the model: their best passages score about 0.65, above the 0.55 gate.

## Layout

```
backend/ChatBot.Api/
  Domain/      ChatMessage, ChatSession, SourceRef - the conversation itself
  Sessions/    storage seam, in-memory implementation, idle eviction
  Llm/         chat template, context-window policy, LLamaSharp wrapper
  Rag/         options, the document library (scan, uploads, scope, prompts)
  Chat/        order of operations for answering a message, citation checks
  Controllers/ HTTP surface including the SSE stream and /api/documents
backend/ChatBot.Rag/        shared by the API, Ingest and Eval
  Ingestion/   PdfPig reader, page classifier, table stitcher, text cleaner,
               DesignedLayout (call-outs, note markers, table and chart regions),
               IngestPipeline (PDF -> index folder, used by Ingest and the API)
  Chunking/    sentence splitter, section chunker
  Embeddings/  bge-small through LLamaSharp
  Index/       index files, flat vector search, BM25
  Retrieval/   query builder, section router, rank fusion, hybrid retriever,
               passage trimmer
backend/ChatBot.Ingest/     PDFs -> data/index/<docId>/ (manifest, chunks, vectors);
                            --fonts / --parse-only for writing profiles
backend/ChatBot.Eval/       scores retrieval and answers against data/eval/*.json
chatbot-web/src/app/
  core/        API client, signal store, markdown renderer
  components/  sidebar, report list, chat panel, scope menu, message bubble, composer,
               source panel
data/source/   the three reports and their ingest profiles
data/uploads/  PDFs uploaded through the API (git-ignored)
data/eval/     evaluation questions with gold pages and expected facts
docs/ARCHITECTURE.md    diagrams, technology and package inventory
docs/IMPLEMENTATION.md  how each requirement is built, and why
scripts/download-model.ps1
```

## API

| Method | Route | Purpose |
|---|---|---|
| `POST` | `/api/sessions` | Start a conversation, optionally `{ documentIds }`, returns its id |
| `PUT` | `/api/sessions/{id}/documents` | Attach documents (`[]` = all; `useDocuments: false` = general chat) |
| `GET` | `/api/sessions` | List conversations |
| `GET` | `/api/sessions/{id}` | Full transcript |
| `DELETE` | `/api/sessions/{id}` | Discard a conversation |
| `POST` | `/api/sessions/{id}/messages` | Send a message, streams `context`, `sources`, `chunk`, `done` as SSE |
| `GET` | `/api/model/status` | Whether the weights and the document library are loaded |
| `GET` | `/api/documents` | The library: every document with its status, pages, chunks |
| `POST` | `/api/documents` | Upload a PDF (multipart `file`); 202, indexed in the background |
| `DELETE` | `/api/documents/{docId}` | Remove an uploaded document and its index |
| `GET` | `/api/documents/{docId}/chunks/{chunkId}` | Full text behind a citation |
| `GET` | `/api/documents/{docId}/file#page=n` | The PDF, opened on a cited page |
| `POST` | `/api/documents/search` | Retrieval only - what a question would find, with scores (`documentIds` optional) |

## Using a different model

`Llama:ModelPath` in `appsettings.json` takes any GGUF. The 1.5B default was chosen for
a 4-core laptop CPU with no usable GPU offload; it runs at roughly 3-7 tokens/sec. A 3B
or 7B gives noticeably better answers at 2-3x the latency. If the new model isn't a
ChatML family (Qwen, Yi, Hermes), you also need an `IChatTemplate` implementation for its
prompt format.

## Known limitations

Conversations live in process memory and are lost on restart (documents and their indexes
are kept on disk). There's no authentication, so anyone who can reach the API can read
any session id they can guess, and the document library is shared by everyone. Inference
is serialised, one reply at a time, and indexing an upload shares the CPU with it, so an
answer asked during a large upload is slower. All of this is deliberate for a single-node
demo and covered in `docs/IMPLEMENTATION.md`.

Charts are read only through their text: titles, axis labels and printed call-out
figures. A bar with no number printed on it is invisible, since there's no vision model.
Table and figure regions in a profile are boxes in PDF points, measured once with
`--fonts --pages`; a new edition with a different layout needs them measured again.

Answer time on CPU is almost all prompt reading, at ~46 tokens/s for the 1.5B model;
writing runs at ~20 tokens/s after that. `Rag:Retrieval:PassageMaxChars` (700) cuts each
retrieved passage after the first (`WholePassages`: 1) to the sentences or table rows
that share the question's words before it goes into the prompt. Measured on the 29 GPO
eval questions:

| Setting | First word, median | Passed |
|---|---|---|
| `PassageMaxChars: 0` (whole passages) | 40.7 s | 20/29 |
| `PassageMaxChars: 700`, `WholePassages: 1` (default) | 22.0 s | 17-18/29 |
| `PassageMaxChars: 700`, `WholePassages: 0` | 24.5 s | 15/29 |

The lost questions are mostly the 1.5B model replying with nothing but "[1]" to a short
prompt; the server then quotes the source sentence that best matches the question, which
is not always the one with the answer. Set `PassageMaxChars` to 0 when accuracy matters
more than the wait. 8 threads and flash attention on or off were measured and were all
slower than the defaults on a 4-core i5; a GPU is what would move it further.
