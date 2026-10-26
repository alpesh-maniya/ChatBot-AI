import { Injectable, inject } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { firstValueFrom } from 'rxjs';
import type {
  ChatEvent,
  ModelStatus,
  SessionDetail,
  SessionSummary,
} from './chat.models';

/**
 * Transport only - state lives in the store.
 *
 * HttpClient for the ordinary JSON calls, raw fetch() for the streaming one.
 * HttpClient buffers the whole body before it emits, which is useless for a token
 * stream; fetch() gives you a ReadableStream. EventSource is GET-only and the message
 * has to go in a body, so that was out too.
 */
@Injectable({ providedIn: 'root' })
export class ChatApi {
  private readonly http = inject(HttpClient);
  private readonly base = '/api';

  getModelStatus(): Promise<ModelStatus> {
    return firstValueFrom(this.http.get<ModelStatus>(`${this.base}/model/status`));
  }

  listSessions(): Promise<SessionSummary[]> {
    return firstValueFrom(this.http.get<SessionSummary[]>(`${this.base}/sessions`));
  }

  createSession(): Promise<SessionSummary> {
    return firstValueFrom(this.http.post<SessionSummary>(`${this.base}/sessions`, {}));
  }

  getSession(id: string): Promise<SessionDetail> {
    return firstValueFrom(this.http.get<SessionDetail>(`${this.base}/sessions/${id}`));
  }

  deleteSession(id: string): Promise<void> {
    return firstValueFrom(this.http.delete<void>(`${this.base}/sessions/${id}`));
  }

  /**
   * Yields one object per server event, in order. Aborting the signal closes the
   * connection; the server stops generating and keeps what it already produced.
   */
  async *streamMessage(
    sessionId: string,
    message: string,
    signal: AbortSignal,
  ): AsyncGenerator<ChatEvent> {
    const response = await fetch(`${this.base}/sessions/${sessionId}/messages`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json', Accept: 'text/event-stream' },
      body: JSON.stringify({ message }),
      signal,
    });

    if (!response.ok) {
      const detail = await response.json().catch(() => null);
      throw new Error(detail?.detail ?? `Request failed with status ${response.status}.`);
    }
    if (!response.body) {
      throw new Error('This browser did not provide a readable response body.');
    }

    const reader = response.body.getReader();
    const decoder = new TextDecoder();
    let buffer = '';

    try {
      while (true) {
        const { done, value } = await reader.read();
        if (done) break;

        // stream: true because a multi-byte UTF-8 character can straddle two network
        // chunks, and decoding them independently corrupts it.
        buffer += decoder.decode(value, { stream: true });

        // Records are separated by a blank line; anything after the last one is
        // partial and waits for the next read.
        let split: number;
        while ((split = buffer.indexOf('\n\n')) >= 0) {
          const raw = buffer.slice(0, split);
          buffer = buffer.slice(split + 2);

          const event = parseEvent(raw);
          if (event) yield event;
        }
      }
    } finally {
      reader.releaseLock();
    }
  }
}

function parseEvent(raw: string): ChatEvent | null {
  let name = 'message';
  const dataLines: string[] = [];

  for (const line of raw.split('\n')) {
    if (line.startsWith('event:')) {
      name = line.slice(6).trim();
    } else if (line.startsWith('data:')) {
      dataLines.push(line.slice(5).trimStart());
    }
  }

  if (!dataLines.length) return null;

  try {
    const data = JSON.parse(dataLines.join('\n'));
    switch (name) {
      case 'context':
      case 'chunk':
      case 'done':
      case 'error':
        return { type: name, data } as ChatEvent;
      default:
        return null;
    }
  } catch {
    // One bad record shouldn't kill the conversation.
    console.warn('Discarded unparseable SSE record:', raw);
    return null;
  }
}
