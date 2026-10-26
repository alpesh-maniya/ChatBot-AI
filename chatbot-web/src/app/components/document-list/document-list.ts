import { ChangeDetectionStrategy, Component, inject, input } from '@angular/core';
import type { DocumentInfo } from '../../core/chat.models';
import { ChatStore } from '../../core/chat-store';
import { documentMeta, isPending, progressPercent } from '../../core/documents';

// The reports in the library. Read-only: which ones a chat searches is picked in the
// chat's header menu, and the ones it doesn't search are dimmed here.
@Component({
  selector: 'app-document-list',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <section class="library" aria-label="Reports">
      <header class="library__header">
        <span class="library__heading">Knowledge base</span>
        <span class="library__count">{{ documents().length }}</span>
      </header>

      <ul class="library__list">
        @for (doc of documents(); track doc.docId) {
          <li
            class="item"
            [attr.data-tone]="store.toneOf(doc.docId)"
            [class.item--off]="!activeIds().includes(doc.docId)"
            [class.item--failed]="doc.status === 'Failed'"
            [title]="(doc.error ?? doc.fileName) + (activeIds().includes(doc.docId) ? '' : ' - not searched in this chat')"
          >
            <span class="item__icon" [class.item__icon--pending]="pending(doc)" aria-hidden="true">
              <svg viewBox="0 0 24 24" width="14" height="14">
                <path d="M7 3h7l5 5v12a1 1 0 0 1-1 1H7a1 1 0 0 1-1-1V4a1 1 0 0 1 1-1Zm7 0v5h5" stroke="currentColor" stroke-width="1.8" fill="none" stroke-linejoin="round" />
              </svg>
            </span>
            <span class="item__text">
              <span class="item__title">{{ doc.title }}</span>
              <span class="item__meta">{{ meta(doc) }}</span>
              @if (pending(doc)) {
                <span class="item__bar" aria-hidden="true">
                  <span class="item__fill" [style.width.%]="percent(doc)"></span>
                </span>
              }
            </span>
          </li>
        } @empty {
          <li class="library__empty">Loading the reports...</li>
        }
      </ul>
    </section>
  `,
  styleUrl: './document-list.scss',
})
export class DocumentListComponent {
  protected readonly store = inject(ChatStore);

  readonly documents = input.required<DocumentInfo[]>();
  /** The reports the open chat searches; the rest are shown dimmed. */
  readonly activeIds = input<string[]>([]);

  protected readonly meta = documentMeta;
  protected readonly pending = isPending;
  protected readonly percent = progressPercent;
}
