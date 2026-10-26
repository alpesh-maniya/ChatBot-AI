import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  computed,
  input,
  output,
  signal,
  viewChild,
} from '@angular/core';

// Presentational only: emits what was typed, and is told whether sending is allowed.
// Knows nothing about sessions or the API.
@Component({
  selector: 'app-composer',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <form class="composer" (submit)="submit($event)">
      <textarea
        #input
        class="composer__input"
        rows="1"
        [placeholder]="placeholder()"
        [disabled]="disabled()"
        [value]="draft()"
        (input)="onInput($event)"
        (keydown)="onKeydown($event)"
        aria-label="Message"
      ></textarea>

      @if (generating()) {
        <button type="button" class="composer__button composer__button--stop" (click)="stop.emit()">
          Stop
        </button>
      } @else {
        <button type="submit" class="composer__button" [disabled]="!canSubmit()" aria-label="Send">
          <svg viewBox="0 0 24 24" width="18" height="18" aria-hidden="true">
            <path d="M3 11.5 21 3l-8.5 18-2-7.5-7.5-2Z" fill="currentColor" />
          </svg>
        </button>
      }
    </form>

    <p class="composer__hint">Enter to send &middot; Shift+Enter for a new line &middot; {{ hint() }}</p>
  `,
  styleUrl: './composer.scss',
})
export class ComposerComponent {
  readonly disabled = input(false);
  readonly generating = input(false);
  readonly placeholder = input('Message the assistant...');
  readonly hint = input('');

  readonly send = output<string>();
  readonly stop = output<void>();

  private readonly inputRef = viewChild.required<ElementRef<HTMLTextAreaElement>>('input');
  protected readonly draft = signal('');
  protected readonly canSubmit = computed(() => !this.disabled() && this.draft().trim().length > 0);

  protected onInput(event: Event): void {
    const el = event.target as HTMLTextAreaElement;
    this.draft.set(el.value);
    this.autoGrow(el);
  }

  protected onKeydown(event: KeyboardEvent): void {
    // Enter sends; Shift+Enter inserts a newline. isComposing guards IME input - without
    // it, confirming a candidate in a Japanese or Chinese IME would send the message.
    if (event.key === 'Enter' && !event.shiftKey && !event.isComposing) {
      event.preventDefault();
      this.emit();
    }
  }

  protected submit(event: Event): void {
    event.preventDefault();
    this.emit();
  }

  private emit(): void {
    if (!this.canSubmit()) return;

    this.send.emit(this.draft().trim());
    this.draft.set('');

    const el = this.inputRef().nativeElement;
    el.value = '';
    this.autoGrow(el);
    el.focus();
  }

  /** Grows the textarea with its content up to a ceiling, then scrolls. */
  private autoGrow(el: HTMLTextAreaElement): void {
    el.style.height = 'auto';
    el.style.height = `${Math.min(el.scrollHeight, 200)}px`;
  }
}
