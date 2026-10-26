import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { renderMarkdown } from '../../core/markdown';
import type { ChatRole } from '../../core/chat.models';

// Bound with [innerHTML], not bypassSecurityTrustHtml, so Angular's sanitiser still
// runs over our own output. Two passes over untrusted model text for no real cost.
@Component({
  selector: 'app-message',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <article class="message" [class.message--user]="role() === 'user'">
      <div class="message__avatar" [attr.aria-hidden]="true">
        {{ role() === 'user' ? 'You' : 'AI' }}
      </div>

      <div class="message__body">
        @if (role() === 'user') {
          <p class="message__plain">{{ content() }}</p>
        } @else {
          <div class="message__markdown" [innerHTML]="html()"></div>
        }

        @if (pending()) {
          <span class="message__caret" aria-label="Generating"></span>
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

  // computed() memoises on content(), so this doesn't re-render markdown on every
  // change detection pass - only when the text actually grows.
  protected readonly html = computed(() => renderMarkdown(this.content()));
}
