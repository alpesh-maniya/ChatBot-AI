import { ChangeDetectionStrategy, Component, ElementRef, HostListener, computed, inject, input, output, signal } from '@angular/core';
import type { DocumentInfo } from '../../core/chat.models';
import { ChatStore } from '../../core/chat-store';
import { documentMeta } from '../../core/documents';

// What the open chat answers from: general chat, or any mix of the reports, as a
// two-way switch whose Reports side opens the list. The choice belongs to the chat (the
// server keeps it per session), so it changes the next question and every one after,
// not the ones already answered.
@Component({
  selector: 'app-scope-menu',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="switch" role="radiogroup" aria-label="What this chat answers from">
      <button
        type="button"
        role="radio"
        class="seg"
        [class.seg--on]="general()"
        [attr.aria-checked]="general()"
        title="The model on its own: no report is searched, nothing is cited"
        (click)="pickGeneral()"
      >
        <svg viewBox="0 0 24 24" width="15" height="15" aria-hidden="true">
          <path d="M4 5.5A2.5 2.5 0 0 1 6.5 3h11A2.5 2.5 0 0 1 20 5.5v8a2.5 2.5 0 0 1-2.5 2.5H10l-4.5 4v-4A2.5 2.5 0 0 1 4 13.5v-8Z" stroke="currentColor" stroke-width="1.8" fill="none" stroke-linejoin="round" />
        </svg>
        General chat
      </button>

      <button
        type="button"
        role="radio"
        class="seg seg--reports"
        [class.seg--on]="!general()"
        [class.seg--open]="open()"
        [attr.aria-checked]="!general()"
        [attr.aria-expanded]="open()"
        aria-haspopup="menu"
        (click)="pickReportsMode()"
      >
        @if (general()) {
          <svg viewBox="0 0 24 24" width="15" height="15" aria-hidden="true">
            <path d="M7 3h7l5 5v12a1 1 0 0 1-1 1H7a1 1 0 0 1-1-1V4a1 1 0 0 1 1-1Zm7 0v5h5" stroke="currentColor" stroke-width="1.8" fill="none" stroke-linejoin="round" />
          </svg>
          <span class="seg__label">Reports</span>
        } @else {
          <span class="dots" aria-hidden="true">
            @for (id of activeIds(); track id) {
              <span class="dot" [attr.data-tone]="store.toneOf(id)"></span>
            }
          </span>
          <span class="seg__label">{{ label() }}</span>
        }
        <svg class="seg__chevron" viewBox="0 0 24 24" width="14" height="14" aria-hidden="true">
          <path d="M6 9l6 6 6-6" stroke="currentColor" stroke-width="2" fill="none" stroke-linecap="round" stroke-linejoin="round" />
        </svg>
      </button>
    </div>

    @if (open()) {
      <div class="menu" role="menu">
        <div class="menu__heading">
          Answer from reports
          @if (documents().length > 1) {
            <button type="button" class="menu__all" [disabled]="!general() && activeIds().length === documents().length" (click)="pickAll()">
              Select all
            </button>
          }
        </div>

        @for (doc of documents(); track doc.docId) {
          <button
            type="button"
            role="menuitemcheckbox"
            class="option"
            [attr.data-tone]="store.toneOf(doc.docId)"
            [class.option--on]="isOn(doc)"
            [attr.aria-checked]="isOn(doc)"
            [disabled]="doc.status !== 'Ready' || (isOn(doc) && activeIds().length === 1)"
            [title]="isOn(doc) && activeIds().length === 1 ? 'A chat on reports needs at least one; pick General chat for none' : doc.fileName"
            (click)="toggle(doc)"
          >
            <span class="option__mark option__mark--check" aria-hidden="true">
              <svg viewBox="0 0 24 24" width="12" height="12">
                <path d="M5 12.5l4.5 4.5L19 7.5" stroke="currentColor" stroke-width="3" fill="none" stroke-linecap="round" stroke-linejoin="round" />
              </svg>
            </span>
            <span class="option__text">
              <span class="option__title">{{ doc.title }}</span>
              <span class="option__meta">{{ meta(doc) }}</span>
            </span>
          </button>
        }

        <p class="menu__note">Saved with this chat. Earlier answers keep their sources.</p>
      </div>
    }
  `,
  styleUrl: './scope-menu.scss',
})
export class ScopeMenuComponent {
  protected readonly store = inject(ChatStore);
  private readonly host = inject(ElementRef<HTMLElement>);

  readonly documents = input.required<DocumentInfo[]>();
  readonly activeIds = input<string[]>([]);
  readonly general = input(false);

  readonly useReports = output<string[]>();
  readonly useGeneral = output<void>();

  protected readonly open = signal(false);
  protected readonly meta = documentMeta;

  protected readonly label = computed(() => this.store.scopeLabel(this.activeIds(), !this.general()));

  protected isOn(doc: DocumentInfo): boolean {
    return !this.general() && this.activeIds().includes(doc.docId);
  }

  protected pickGeneral(): void {
    if (!this.general()) this.useGeneral.emit();
    this.open.set(false);
  }

  // From general chat: switch to every report, then show the list to narrow it down.
  protected pickReportsMode(): void {
    if (this.general()) this.pickAll();
    this.open.set(!this.open() || this.general());
  }

  protected pickAll(): void {
    this.useReports.emit(this.documents().filter((d) => d.status === 'Ready').map((d) => d.docId));
  }

  // From general chat, a report click means "just this one". Otherwise it adds or removes.
  protected toggle(doc: DocumentInfo): void {
    if (this.general()) {
      this.useReports.emit([doc.docId]);
      return;
    }
    const ids = this.activeIds();
    const next = ids.includes(doc.docId) ? ids.filter((id) => id !== doc.docId) : [...ids, doc.docId];
    if (next.length > 0) this.useReports.emit(next);
  }

  @HostListener('document:click', ['$event'])
  protected onDocumentClick(event: MouseEvent): void {
    if (this.open() && !this.host.nativeElement.contains(event.target as Node)) this.open.set(false);
  }

  @HostListener('document:keydown.escape')
  protected onEscape(): void {
    this.open.set(false);
  }
}
