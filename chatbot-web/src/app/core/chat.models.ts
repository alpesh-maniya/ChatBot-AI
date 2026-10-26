// Mirrors ChatBot.Api.Contracts. Hand-written rather than generated - it's short
// enough that the client's whole view of the API fits in one readable file.

export type ChatRole = 'system' | 'user' | 'assistant';

export interface ChatMessage {
  id: string;
  role: ChatRole;
  content: string;
  createdAt: string;
}

export interface SessionSummary {
  id: string;
  title: string | null;
  createdAt: string;
  lastActivityAt: string;
  messageCount: number;
}

export interface SessionDetail {
  id: string;
  title: string | null;
  createdAt: string;
  messages: ChatMessage[];
}

/** What the server showed the model this turn. Surfaced in the UI so the context
 *  window is observable instead of mysterious. */
export interface ContextInfo {
  includedMessages: number;
  droppedMessages: number;
  promptTokens: number;
  responseBudget: number;
  contentTruncated: boolean;
}

export interface CompletionInfo {
  messageId: string;
  completionTokens: number;
  elapsedSeconds: number;
  tokensPerSecond: number;
}

export interface ModelStatus {
  model: string;
  ready: boolean;
  contextSize: number;
  parameterCount: number;
  error: string | null;
}

/** Discriminated union of the SSE events the chat endpoint emits. */
export type ChatEvent =
  | { type: 'context'; data: ContextInfo }
  | { type: 'chunk'; data: { text: string } }
  | { type: 'done'; data: CompletionInfo }
  | { type: 'error'; data: { message: string; code: string } };
