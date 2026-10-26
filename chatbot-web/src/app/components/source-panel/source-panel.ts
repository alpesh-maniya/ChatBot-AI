import { ChangeDetectionStrategy, Component, HostListener, computed, inject } from '@angular/core';
import { ChatStore } from '../../core/chat-store';

// Drawer showing the full passage behind a citation, with a link that opens the report
// PDF on the cited page. Opens on the excerpt we already have; the full chunk replaces
// it when the request returns.
@Component({
  selector: 'app-source-panel',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (store.openSource(); as open) {
      <div class="backdrop" (click)="store.closeSource()"></div>
      <aside class="panel" role="dialog" aria-modal="true" [attr.data-tone]="store.toneOf(open.source.docId)" [attr.aria-label]="'Source ' + open.source.n">
        <header class="panel__header">
          <span class="panel__n">{{ open.source.n }}</span>
          <div class="panel__titles">
            @if (open.source.docTitle) {
              <p class="panel__page">{{ open.source.docTitle }}</p>
            }
            <h3 class="panel__section">{{ open.source.section || 'Passage' }}</h3>
            <p class="panel__page">{{ page() }}</p>
          </div>
          <button type="button" class="panel__close" (click)="store.closeSource()" aria-label="Close source">
            <svg viewBox="0 0 24 24" width="16" height="16" aria-hidden="true">
              <path d="M6 6l12 12M18 6L6 18" stroke="currentColor" stroke-width="2" stroke-linecap="round" />
            </svg>
          </button>
        </header>

        <div class="panel__body">
          @if (open.chunk; as chunk) {
            @if (chunk.unit) {
              <p class="panel__unit">Amounts {{ chunk.unit }}</p>
            }
            <pre class="panel__text" [class.panel__text--table]="chunk.kind !== 'Text'">{{ chunk.text }}</pre>
          } @else {
            <p class="panel__text">{{ open.source.excerpt }}</p>
            @if (open.error) {
              <p class="panel__error">{{ open.error }}</p>
            } @else {
              <p class="panel__loading">Loading the full passage...</p>
            }
          }
        </div>

        <footer class="panel__footer">
          <a class="panel__pdf" [href]="store.pdfUrl(open.source)" target="_blank" rel="noopener">
            Open PDF at page {{ open.source.pdfPageStart }}
            <svg viewBox="0 0 24 24" width="14" height="14" aria-hidden="true">
              <path d="M14 4h6v6M20 4l-9 9M18 14v5a1 1 0 0 1-1 1H5a1 1 0 0 1-1-1V7a1 1 0 0 1 1-1h5" stroke="currentColor" stroke-width="2" fill="none" stroke-linecap="round" stroke-linejoin="round" />
            </svg>
          </a>
          @if (open.source.vectorScore !== null) {
            <span class="panel__score" title="Cosine similarity between the question and this passage">
              match {{ open.source.vectorScore.toFixed(2) }}
            </span>
          }
        </footer>
      </aside>
    }
  `,
  styleUrl: './source-panel.scss',
})
export class SourcePanelComponent {
  protected readonly store = inject(ChatStore);

  // Printed page as the report shows it, plus the PDF page when they differ.
  protected readonly page = computed(() => {
    const s = this.store.openSource()?.source;
    if (!s) return '';
    const pdf = s.pdfPageStart === s.pdfPageEnd ? `PDF p. ${s.pdfPageStart}` : `PDF pp. ${s.pdfPageStart}-${s.pdfPageEnd}`;
    if (!s.printedPages) return pdf;
    return `${s.partLabel ? s.partLabel + ' ' : ''}p. ${s.printedPages} · ${pdf}`;
  });

  @HostListener('document:keydown.escape')
  protected onEscape(): void {
    this.store.closeSource();
  }
}
