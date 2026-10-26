import { Injectable, computed, inject, signal } from '@angular/core';
import { ChatApi } from './chat-api';
import type {
  ChatMessage,
  ContextInfo,
  ModelStatus,
  SessionSummary,
} from './chat.models';

/**
 * Single source of truth for the UI.
 *
 * Signals rather than RxJS subjects: the app is zoneless, so a signal write is what
 * schedules rendering. A subject written to from a fetch callback updates nothing
 * without an explicit markForCheck.
 *
 * The in-flight reply lives in its own signal rather than in the message list - it has
 * no server id yet and might be cancelled - which keeps every append to that list final.
 */
@Injectable({ providedIn: 'root' })
export class ChatStore {
  private readonly api = inject(ChatApi);
  private abortController: AbortController | null = null;

  readonly sessions = signal<SessionSummary[]>([]);
  readonly activeSessionId = signal<string | null>(null);
  readonly messages = signal<ChatMessage[]>([]);
  readonly streamingText = signal('');
  readonly isGenerating = signal(false);
  readonly modelStatus = signal<ModelStatus | null>(null);
  readonly lastContext = signal<ContextInfo | null>(null);
  readonly error = signal<string | null>(null);

  readonly modelReady = computed(() => this.modelStatus()?.ready === true);

  readonly canSend = computed(
    () => this.modelReady() && !this.isGenerating() && this.activeSessionId() !== null,
  );

  async initialise(): Promise<void> {
    await Promise.all([this.refreshSessions(), this.pollModelUntilReady()]);

    if (!this.activeSessionId()) {
      await this.startNewSession();
    }
  }

  // Polls until the weights are up. Keeps the composer disabled with a real
  // explanation instead of accepting input that would immediately 503.
  private async pollModelUntilReady(): Promise<void> {
    for (let attempt = 0; attempt < 120; attempt++) {
      try {
        const status = await this.api.getModelStatus();
        this.modelStatus.set(status);
        if (status.ready || status.error) return;
      } catch {
        this.modelStatus.set(null);
      }
      await new Promise((resolve) => setTimeout(resolve, 1000));
    }
  }

  async refreshSessions(): Promise<void> {
    try {
      this.sessions.set(await this.api.listSessions());
    } catch {
      this.error.set('Could not reach the API. Is the backend running?');
    }
  }

  // "Start fresh" is just a new server-side session id: no client state carries over,
  // and the server keys history by that id alone.
  async startNewSession(): Promise<void> {
    this.stop();
    try {
      const session = await this.api.createSession();
      this.sessions.update((all) => [session, ...all]);
      this.activeSessionId.set(session.id);
      this.messages.set([]);
      this.lastContext.set(null);
      this.error.set(null);
    } catch {
      this.error.set('Could not start a new conversation.');
    }
  }

  async selectSession(id: string): Promise<void> {
    if (id === this.activeSessionId()) return;

    this.stop();
    try {
      const detail = await this.api.getSession(id);
      this.activeSessionId.set(detail.id);
      this.messages.set(detail.messages);
      this.lastContext.set(null);
      this.error.set(null);
    } catch {
      this.error.set('That conversation is no longer available.');
      await this.refreshSessions();
    }
  }

  async deleteSession(id: string): Promise<void> {
    await this.api.deleteSession(id).catch(() => undefined);
    this.sessions.update((all) => all.filter((s) => s.id !== id));

    if (this.activeSessionId() === id) {
      await this.startNewSession();
    }
  }

  async send(text: string): Promise<void> {
    const sessionId = this.activeSessionId();
    const message = text.trim();
    if (!sessionId || !message || this.isGenerating()) return;

    // Shown right away. The server appends the same turn to its own history, so this is
    // a display shortcut rather than a second source of truth.
    this.messages.update((all) => [
      ...all,
      {
        id: `local-${crypto.randomUUID()}`,
        role: 'user',
        content: message,
        createdAt: new Date().toISOString(),
      },
    ]);

    this.isGenerating.set(true);
    this.streamingText.set('');
    this.error.set(null);

    this.abortController = new AbortController();
    let streamed = '';

    try {
      for await (const event of this.api.streamMessage(sessionId, message, this.abortController.signal)) {
        switch (event.type) {
          case 'context':
            this.lastContext.set(event.data);
            break;

          case 'chunk':
            streamed += event.data.text;
            this.streamingText.set(streamed);
            break;

          case 'done':
            this.messages.update((all) => [
              ...all,
              {
                id: event.data.messageId,
                role: 'assistant',
                content: streamed,
                createdAt: new Date().toISOString(),
              },
            ]);
            this.streamingText.set('');
            break;

          case 'error':
            this.error.set(event.data.message);
            break;
        }
      }
    } catch (err) {
      // An abort is the user pressing Stop, not a failure.
      if ((err as Error)?.name !== 'AbortError') {
        this.error.set((err as Error).message || 'The request failed.');
      }
    } finally {
      // Keep whatever arrived before a stop; the server kept it too.
      if (streamed && this.streamingText()) {
        this.messages.update((all) => [
          ...all,
          {
            id: `partial-${crypto.randomUUID()}`,
            role: 'assistant',
            content: streamed,
            createdAt: new Date().toISOString(),
          },
        ]);
      }

      this.streamingText.set('');
      this.isGenerating.set(false);
      this.abortController = null;
      void this.refreshSessions();
    }
  }

  stop(): void {
    this.abortController?.abort();
    this.abortController = null;
  }
}
