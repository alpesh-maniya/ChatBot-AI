import {
  AfterViewInit,
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  effect,
  inject,
  viewChild,
} from '@angular/core';
import { DecimalPipe } from '@angular/common';
import { ChatStore } from '../../core/chat-store';
import { MessageComponent } from '../message/message';
import { ComposerComponent } from '../composer/composer';

// Transcript plus composer. Autoscroll sticks to the bottom unless the user has
// scrolled up - scrolling unconditionally yanks the view down on every token, so you
// can't read back through a long answer while it's still being written.
@Component({
  selector: 'app-chat-panel',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [DecimalPipe, MessageComponent, ComposerComponent],
  templateUrl: './chat-panel.html',
  styleUrl: './chat-panel.scss',
})
export class ChatPanelComponent implements AfterViewInit {
  protected readonly store = inject(ChatStore);

  private readonly scroller = viewChild.required<ElementRef<HTMLElement>>('scroller');
  private stickToBottom = true;

  constructor() {
    // Re-runs on every streamed token.
    effect(() => {
      this.store.messages();
      this.store.streamingText();

      if (!this.stickToBottom) return;

      // A microtask is enough - the DOM is updated before the browser paints.
      queueMicrotask(() => {
        const el = this.scroller().nativeElement;
        el.scrollTop = el.scrollHeight;
      });
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
        this.stickToBottom = distanceFromBottom < 48;
      },
      { passive: true },
    );
  }

  protected send(text: string): void {
    this.stickToBottom = true;
    void this.store.send(text);
  }

  protected placeholder(): string {
    const status = this.store.modelStatus();
    if (!status) return 'Connecting to the API...';
    if (status.error) return 'The model failed to load.';
    if (!status.ready) return 'Loading the model, one moment...';
    return 'Message the assistant...';
  }
}
