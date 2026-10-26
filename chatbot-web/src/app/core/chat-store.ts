import { Injectable, computed, inject, signal } from '@angular/core';
import { ChatApi } from './chat-api';
import type {
  ChatMessage,
  ChunkDetail,
  CompletionInfo,
  ContextInfo,
  DocumentInfo,
  ModelStatus,
  SessionSummary,
  SourceRef,
  SourcesInfo,
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
  readonly lastCompletion = signal<CompletionInfo | null>(null);
  readonly error = signal<string | null>(null);

  /** Sources of the reply being streamed. They arrive before the first token. */
  readonly streamingSources = signal<SourcesInfo | null>(null);

  /** The citation open in the side panel. */
  readonly openSource = signal<OpenSource | null>(null);

  // With RAG on, the chat model alone isn't enough: questions 503 until the report
  // index is loaded too.
  readonly modelReady = computed(() => {
    const status = this.modelStatus();
    return status?.ready === true && (!status.ragEnabled || status.indexReady);
  });

  /**
   * The reports a chat can search: the PDFs shipped in data/source. Uploads still sit in
   * the API's library from earlier work, but this app doesn't show or search them.
   */
  readonly documents = signal<DocumentInfo[]>([]);

  private readonly reportIds = computed(() => this.documents().map((d) => d.docId));

  /** Colour slot per report, in library order, for chips and cards. */
  toneOf(docId: string): number {
    const i = this.reportIds().indexOf(docId);
    return i < 0 ? 3 : i % 4;
  }

  // What the open chat answers from. Kept on the server per session, so switching chats
  // brings each one's own choice back.
  readonly activeDocumentIds = signal<string[]>([]);
  readonly activeUseDocuments = signal(true);

  /** The model on its own: no report is searched. */
  readonly generalChat = computed(() => !this.modelStatus()?.ragEnabled || !this.activeUseDocuments());

  /** The reports the next question searches, in library order. */
  readonly scopeDocuments = computed(() => {
    if (this.generalChat()) return [];
    const ids = this.activeDocumentIds();
    return this.documents().filter((d) => ids.includes(d.docId));
  });

  /** Why nothing can be asked yet, or null when it can. */
  readonly scopeProblem = computed(() => {
    if (this.generalChat()) return null;
    const scope = this.scopeDocuments();
    if (scope.length === 0) return this.documents().length === 0 ? 'Loading the reports...' : 'Pick at least one report.';
    if (scope.some((d) => d.status === 'Ready')) return null;
    return scope.some((d) => d.status === 'Queued' || d.status === 'Processing')
      ? 'Indexing the reports...'
      : 'The selected reports could not be indexed.';
  });

  readonly canSend = computed(
    () =>
      this.modelReady() &&
      !this.isGenerating() &&
      this.activeSessionId() !== null &&
      this.scopeProblem() === null,
  );

  private documentPoll: ReturnType<typeof setTimeout> | null = null;

  async initialise(): Promise<void> {
    await Promise.all([this.refreshSessions(), this.pollModelUntilReady()]);
    await this.refreshDocuments();

    if (!this.activeSessionId()) {
      // Reloading the page shouldn't leave another empty "New conversation" behind.
      const empty = this.visibleSessions().find((s) => s.messageCount === 0);
      if (empty) await this.selectSession(empty.id);
      else await this.startNewSession();
    }
  }

  // Chats on these reports, or general chat. A session attached to nothing with
  // documents on means "every document, uploads too" - made by scripts, not this app.
  private isOurs(session: SessionSummary): boolean {
    const ids = this.reportIds();
    if (!session.useDocuments) return true;
    return session.documentIds.length > 0 && session.documentIds.every((id) => ids.includes(id));
  }

  readonly visibleSessions = computed(() => this.sessions().filter((s) => this.isOurs(s)));

  /** "General chat", "All 3 reports", "GPO 2018 Annual Report", "2 reports". */
  scopeLabel(documentIds: string[], useDocuments: boolean): string {
    if (!useDocuments) return 'General chat';
    const ids = this.reportIds().filter((id) => documentIds.includes(id));
    if (ids.length === this.reportIds().length && ids.length > 1) return `All ${ids.length} reports`;
    if (ids.length === 1) return this.documents().find((d) => d.docId === ids[0])?.title ?? '1 report';
    return `${ids.length} reports`;
  }

  // Re-reads the library, and keeps doing so every two seconds while anything is still
  // queued or being indexed, so the chips move from "Embedding 40/154" to ready by
  // themselves. Stops polling once everything has settled.
  async refreshDocuments(): Promise<void> {
    if (this.documentPoll) {
      clearTimeout(this.documentPoll);
      this.documentPoll = null;
    }

    try {
      const documents = (await this.api.listDocuments()).filter((d) => d.origin === 'Library');
      this.documents.set(documents);
      if (documents.some((d) => d.status === 'Queued' || d.status === 'Processing')) {
        this.documentPoll = setTimeout(() => void this.refreshDocuments(), 2000);
      }
    } catch {
      // The model status poll already reports an unreachable API.
    }
  }

  /** Search these reports from now on. History stays; follow-ups keep working. */
  async useReports(documentIds: string[]): Promise<void> {
    if (documentIds.length === 0) return;
    await this.saveScope(documentIds, true);
  }

  async useGeneralChat(): Promise<void> {
    await this.saveScope([], false);
  }

  private async saveScope(documentIds: string[], useDocuments: boolean): Promise<void> {
    const sessionId = this.activeSessionId();
    if (!sessionId) return;

    try {
      const session = await this.api.setSessionDocuments(sessionId, documentIds, useDocuments);
      this.applyScope(session);
      this.sessions.update((all) => all.map((s) => (s.id === session.id ? session : s)));
    } catch {
      this.error.set('Could not change what this chat answers from.');
    }
  }

  private applyScope(session: { documentIds: string[]; useDocuments: boolean }): void {
    this.activeDocumentIds.set(session.documentIds ?? []);
    this.activeUseDocuments.set(session.useDocuments ?? true);
  }

  // Polls until the weights are up. Keeps the composer disabled with a real
  // explanation instead of accepting input that would immediately 503.
  private async pollModelUntilReady(): Promise<void> {
    for (let attempt = 0; attempt < 120; attempt++) {
      try {
        const status = await this.api.getModelStatus();
        this.modelStatus.set(status);
        const indexDone = !status.ragEnabled || status.indexReady || !!status.indexError;
        if ((status.ready && indexDone) || status.error) return;
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
  // and the server keys history by that id alone. A new chat starts on the same reports
  // (or general chat) as the one open now; the very first one gets every report.
  async startNewSession(): Promise<void> {
    this.stop();
    const open = this.activeSessionId() !== null;
    const general = open && this.generalChat();
    const current = open ? this.scopeDocuments().map((d) => d.docId) : [];
    const ids = general ? [] : current.length > 0 ? current : this.reportIds();

    try {
      const session = await this.api.createSession(ids, !general);
      this.sessions.update((all) => [session, ...all]);
      this.activeSessionId.set(session.id);
      this.applyScope(session);
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
      this.applyScope(detail);
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
    this.lastCompletion.set(null);
    this.streamingText.set('');
    this.streamingSources.set(null);
    this.error.set(null);

    this.abortController = new AbortController();
    let streamed = '';
    let sources: SourcesInfo | null = null;

    try {
      for await (const event of this.api.streamMessage(sessionId, message, this.abortController.signal)) {
        switch (event.type) {
          case 'context':
            this.lastContext.set(event.data);
            break;

          case 'sources':
            sources = event.data;
            this.streamingSources.set(sources);
            break;

          case 'chunk':
            streamed += event.data.text;
            this.streamingText.set(streamed);
            break;

          case 'done':
            this.lastCompletion.set(event.data);
            // The server's copy wins: it has bad citations removed and missing ones
            // added, which the streamed text doesn't.
            this.messages.update((all) => [
              ...all,
              {
                id: event.data.messageId,
                role: 'assistant',
                content: event.data.content || streamed,
                createdAt: new Date().toISOString(),
                sources: sources?.sources ?? [],
                citedSources: event.data.citedSources,
                grounded: sources?.grounded,
              },
            ]);
            this.streamingText.set('');
            this.streamingSources.set(null);
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
            sources: sources?.sources ?? [],
            grounded: sources?.grounded,
          },
        ]);
      }

      this.streamingText.set('');
      this.streamingSources.set(null);
      this.isGenerating.set(false);
      this.abortController = null;
      void this.refreshSessions();
    }
  }

  stop(): void {
    this.abortController?.abort();
    this.abortController = null;
  }

  // The excerpt is already on the SourceRef, so the panel opens instantly and the full
  // passage fills in when it arrives.
  async showSource(source: SourceRef): Promise<void> {
    this.openSource.set({ source, chunk: null, error: null });
    try {
      const chunk = await this.api.getChunk(source.docId, source.chunkId);
      if (this.openSource()?.source === source) {
        this.openSource.set({ source, chunk, error: null });
      }
    } catch {
      if (this.openSource()?.source === source) {
        this.openSource.set({ source, chunk: null, error: 'Could not load the full passage.' });
      }
    }
  }

  closeSource(): void {
    this.openSource.set(null);
  }

  pdfUrl(source: SourceRef): string {
    return this.api.pdfUrl(source.docId, source.pdfPageStart);
  }
}

export interface OpenSource {
  source: SourceRef;
  chunk: ChunkDetail | null;
  error: string | null;
}
