import {
  AfterViewInit,
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  computed,
  effect,
  inject,
  signal,
  viewChild,
} from '@angular/core';
import { DecimalPipe } from '@angular/common';
import { ChatStore } from '../../core/chat-store';
import { documentMeta } from '../../core/documents';
import { MessageComponent } from '../message/message';
import { ComposerComponent } from '../composer/composer';
import { ScopeMenuComponent } from '../scope-menu/scope-menu';

// Transcript plus composer. Autoscroll sticks to the bottom unless the user has
// scrolled up - scrolling unconditionally yanks the view down on every token, so you
// can't read back through a long answer while it's still being written.
//
// Each chat answers from the reports picked in the header menu (all of them by default),
// or is a general chat. The welcome cards follow that choice.
@Component({
  selector: 'app-chat-panel',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [DecimalPipe, MessageComponent, ComposerComponent, ScopeMenuComponent],
  templateUrl: './chat-panel.html',
  styleUrl: './chat-panel.scss',
})
export class ChatPanelComponent implements AfterViewInit {
  protected readonly store = inject(ChatStore);

  private readonly scroller = viewChild.required<ElementRef<HTMLElement>>('scroller');

  // A signal (not a plain field) because the "jump to latest" button renders off it.
  protected readonly atBottom = signal(true);

  protected readonly meta = documentMeta;

  protected readonly scopeIds = computed(() => this.store.scopeDocuments().map((d) => d.docId));

  /** Starter questions for the reports in this chat: three for one report, two each for more. */
  protected readonly cards = computed(() => {
    const scope = this.store.scopeDocuments();
    return scope.map((doc) => ({
      doc,
      tone: this.store.toneOf(doc.docId),
      questions: doc.exampleQuestions.slice(0, scope.length === 1 ? 3 : 2),
    }));
  });

  protected readonly generalExamples = GENERAL_EXAMPLES;

  protected readonly heading = computed(() => {
    if (this.store.generalChat()) return 'How can I help?';
    const scope = this.store.scopeDocuments();
    return scope.length === 1 ? `Ask about the ${scope[0].title}` : 'What would you like to know?';
  });

  protected readonly subtitle = computed(() => {
    if (this.store.generalChat()) {
      return 'General chat: the model answers from what it already knows, and nothing is looked up, so check the facts that matter. Switch to Reports above for answers with page citations.';
    }
    const scope = this.store.scopeDocuments();
    return scope.length === 1
      ? 'Every answer comes from this report and cites the page. Follow-up questions keep the context.'
      : `The assistant works out which of the ${scope.length} reports has the answer, and every answer cites the page. Follow-up questions keep the context.`;
  });

  protected readonly title = computed(() => {
    const id = this.store.activeSessionId();
    const session = this.store.sessions().find((s) => s.id === id);
    return session?.title ?? 'New conversation';
  });

  /** Seconds since the question was sent, until the first word arrives. */
  protected readonly waited = signal(0);

  constructor() {
    // A visible counter, because 20-40 s of silence on CPU otherwise looks like a hang.
    effect((onCleanup) => {
      if (!this.store.isGenerating() || this.store.streamingText()) return;
      const started = Date.now();
      this.waited.set(0);
      const timer = setInterval(() => this.waited.set(Math.floor((Date.now() - started) / 1000)), 1000);
      onCleanup(() => clearInterval(timer));
    });

    // Re-runs on every streamed token.
    effect(() => {
      this.store.messages();
      this.store.streamingText();

      if (!this.atBottom()) return;

      // A microtask is enough - the DOM is updated before the browser paints.
      queueMicrotask(() => this.scrollToBottom());
    });
  }

  ngAfterViewInit(): void {
    const el = this.scroller().nativeElement;
    el.addEventListener(
      'scroll',
      () => {
        // 48px of slack - these numbers rarely agree exactly once sub-pixel layout
        // and momentum scrolling are in play.
        const distanceFromBottom = el.scrollHeight - el.scrollTop - el.clientHeight;
        this.atBottom.set(distanceFromBottom < 48);
      },
      { passive: true },
    );
  }

  protected scrollToBottom(behavior: ScrollBehavior = 'auto'): void {
    const el = this.scroller().nativeElement;
    el.scrollTo({ top: el.scrollHeight, behavior });
  }

  protected send(text: string): void {
    this.atBottom.set(true);
    void this.store.send(text);
  }

  protected placeholder(): string {
    const status = this.store.modelStatus();
    if (!status) return 'Connecting to the API...';
    if (status.error) return 'The model failed to load.';
    if (status.indexError) return 'The reports failed to load.';
    if (!status.ready) return 'Loading the model, one moment...';
    if (status.ragEnabled && !status.indexReady) return 'Loading the reports, one moment...';
    if (this.store.generalChat()) return 'Message the assistant...';

    const problem = this.store.scopeProblem();
    if (problem) return problem;
    const scope = this.store.scopeDocuments();
    return scope.length === 1 ? `Ask about the ${scope[0].title}...` : 'Ask anything about the selected reports...';
  }

  protected hint(): string {
    if (this.store.generalChat()) return 'general chat, not from the reports';
    return this.store.scopeDocuments().length === 1 ? 'answers come only from this report' : 'answers come only from the selected reports';
  }
}

// An empty general chat. Ordinary things a local assistant can do without documents.
const GENERAL_EXAMPLES = [
  'Explain retrieval-augmented generation in simple terms',
  'Write a short, polite follow-up email after a meeting',
  'Give me a checklist for reviewing a contract',
];
