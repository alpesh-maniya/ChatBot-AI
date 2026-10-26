import { ChangeDetectionStrategy, Component, computed, inject, input, output } from '@angular/core';
import { ChatStore } from '../../core/chat-store';
import { shortTitle } from '../../core/documents';
import { renderMarkdown } from '../../core/markdown';
import type { ChatRole, SourceRef } from '../../core/chat.models';

// Bound with [innerHTML], not bypassSecurityTrustHtml, so Angular's sanitiser still
// runs over our own output. Two passes over untrusted model text for no real cost.
//
// Citations: "[2]" in the reply becomes <sup class="cite">2</sup>. <sup> and class both
// survive the sanitiser (buttons and data-* attributes don't), and one click handler on
// the container reads the number back out of the element.
@Component({
  selector: 'app-message',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <article
      class="message"
      [class.message--user]="role() === 'user'"
      [class.message--notfound]="grounded() === false && sources().length === 0 && role() === 'assistant'"
    >
      @if (role() !== 'user') {
        <div class="message__avatar" aria-hidden="true">
          <svg viewBox="0 0 24 24" width="15" height="15">
            <path
              d="M4 5.5A2.5 2.5 0 0 1 6.5 3h11A2.5 2.5 0 0 1 20 5.5v8a2.5 2.5 0 0 1-2.5 2.5H10l-4.5 4v-4A2.5 2.5 0 0 1 4 13.5v-8Z"
              fill="currentColor"
            />
          </svg>
        </div>
      }

      <div class="message__body">
        @if (role() === 'user') {
          <p class="message__plain">{{ content() }}</p>
        } @else {
          <div class="message__markdown" [innerHTML]="html()" (click)="onCitationClick($event)"></div>
        }

        @if (pending()) {
          <span class="message__caret" aria-label="Generating"></span>
        }

        @if (role() === 'assistant' && sources().length > 0) {
          <div class="sources" [attr.aria-label]="'Sources for this answer'">
            <span class="sources__label">Sources</span>
            @for (source of sources(); track source.chunkId) {
              <button
                type="button"
                class="source"
                [attr.data-tone]="store.toneOf(source.docId)"
                [class.source--cited]="isCited(source.n)"
                [title]="source.excerpt"
                (click)="sourceSelected.emit(source)"
              >
                <span class="source__n">{{ source.n }}</span>
                <span class="source__doc">{{ short(source.docTitle) }}</span>
                <span class="source__label">{{ source.label }}</span>
              </button>
            }
          </div>
        }
      </div>
    </article>
  `,
  styleUrl: './message.scss',
})
export class MessageComponent {
  readonly role = input.required<ChatRole>();
  readonly content = input.required<string>();

  /** True for the reply being streamed; drives the blinking caret. */
  readonly pending = input(false);

  readonly sources = input<SourceRef[]>([]);
  readonly cited = input<number[] | undefined>(undefined);
  readonly grounded = input<boolean | undefined>(undefined);

  readonly sourceSelected = output<SourceRef>();

  protected readonly store = inject(ChatStore);
  protected readonly short = shortTitle;

  // computed() memoises on content(), so this doesn't re-render markdown on every
  // change detection pass - only when the text actually grows.
  protected readonly html = computed(() => {
    const rendered = renderMarkdown(this.content());
    if (this.sources().length === 0) return rendered;

    return rendered.replace(/\[(\d+(?:\s*,\s*\d+)*)\]/g, (_, list: string) =>
      list
        .split(',')
        .map((n) => `<sup class="cite">${n.trim()}</sup>`)
        .join(''),
    );
  });

  protected isCited(n: number): boolean {
    const cited = this.cited();
    // While streaming nothing is validated yet; treat everything as cited.
    return !cited || cited.length === 0 || cited.includes(n);
  }

  protected onCitationClick(event: MouseEvent): void {
    const target = (event.target as HTMLElement | null)?.closest('sup.cite');
    if (!target) return;

    const n = Number(target.textContent);
    const source = this.sources().find((s) => s.n === n);
    if (source) this.sourceSelected.emit(source);
  }
}
