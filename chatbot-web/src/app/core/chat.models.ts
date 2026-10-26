// Mirrors ChatBot.Api.Contracts. Hand-written rather than generated - it's short
// enough that the client's whole view of the API fits in one readable file.

export type ChatRole = 'system' | 'user' | 'assistant';

/** One numbered passage an answer was grounded on - what [n] in the reply points at. */
export interface SourceRef {
  n: number;
  chunkId: string;
  docId: string;
  docTitle: string;
  part: string;
  partLabel: string;
  section: string;
  printedPages: string | null;
  pdfPageStart: number;
  pdfPageEnd: number;
  /** "GPO's Finances > FY 2018 Financial Results, Annual Report p. 23" */
  label: string;
  excerpt: string;
  vectorScore: number | null;
  bm25Rank: number | null;
  fusedScore: number;
}

export interface ChatMessage {
  id: string;
  role: ChatRole;
  content: string;
  createdAt: string;
  sources?: SourceRef[];
  citedSources?: number[];
  /** false = the fixed "not found in the report" reply. Unknown for reloaded history. */
  grounded?: boolean;
}

export interface SessionSummary {
  id: string;
  title: string | null;
  createdAt: string;
  lastActivityAt: string;
  messageCount: number;
  /** Attached documents. Empty = answers come from every ready document. */
  documentIds: string[];
  /** False = general chat, no document search. */
  useDocuments: boolean;
}

export interface SessionDetail {
  id: string;
  title: string | null;
  createdAt: string;
  messages: ChatMessage[];
  documentIds: string[];
  useDocuments: boolean;
}

export type DocumentStatus = 'Queued' | 'Processing' | 'Ready' | 'Failed';

/** One PDF in the library, in whatever state its index is in. */
export interface DocumentInfo {
  docId: string;
  title: string;
  fileName: string;
  origin: 'Library' | 'Upload';
  status: DocumentStatus;
  /** While Processing: "Embedding 40/154". */
  stage: string | null;
  /** 0..1 while Processing. */
  progress: number | null;
  error: string | null;
  canDelete: boolean;
  addedAt: string;
  pdfPageCount: number | null;
  chunkCount: number | null;
  embeddingModel: string | null;
  indexedAt: string | null;
  exampleQuestions: string[];
}

/** What the server showed the model this turn. Surfaced in the UI so the context
 *  window is observable instead of mysterious. */
export interface ContextInfo {
  includedMessages: number;
  droppedMessages: number;
  promptTokens: number;
  responseBudget: number;
  contentTruncated: boolean;
  sourceCount: number;
  sourceTokens: number;
  droppedSources: number;
}

export interface CompletionInfo {
  messageId: string;
  completionTokens: number;
  elapsedSeconds: number;
  tokensPerSecond: number;
  citedSources: number[];
  uncitedSentences: number;
  /** The stored reply; may differ from the streamed text after citation checks. */
  content: string;
  /** Mostly the CPU reading the prompt; grows with the size of the sources. */
  firstTokenSeconds: number;
}

export interface SourcesInfo {
  grounded: boolean;
  sources: SourceRef[];
}

export interface ModelStatus {
  model: string;
  ready: boolean;
  contextSize: number;
  parameterCount: number;
  error: string | null;
  ragEnabled: boolean;
  indexReady: boolean;
  embeddingModel: string | null;
  /** Set when exactly one document is ready. */
  documentTitle: string | null;
  indexError: string | null;
  documentCount: number;
  pendingDocuments: number;
}

/** Full text behind a citation, for the source panel. */
export interface ChunkDetail {
  id: string;
  docId: string;
  kind: string;
  part: string;
  partLabel: string;
  section: string;
  sectionPath: string[];
  pdfPageStart: number;
  pdfPageEnd: number;
  printedPages: string | null;
  unit: string | null;
  header: string;
  text: string;
}

/** Discriminated union of the SSE events the chat endpoint emits. */
export type ChatEvent =
  | { type: 'context'; data: ContextInfo }
  | { type: 'sources'; data: SourcesInfo }
  | { type: 'chunk'; data: { text: string } }
  | { type: 'done'; data: CompletionInfo }
  | { type: 'error'; data: { message: string; code: string } };
