import { ChangeDetectionStrategy, Component, inject, OnInit } from '@angular/core';
import { DecimalPipe } from '@angular/common';
import { ChatStore } from './core/chat-store';
import { SidebarComponent } from './components/sidebar/sidebar';
import { ChatPanelComponent } from './components/chat-panel/chat-panel';

// Shell: conversation list on the left, active conversation on the right.
@Component({
  selector: 'app-root',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [DecimalPipe, SidebarComponent, ChatPanelComponent],
  template: `
    <div class="shell">
      <app-sidebar
        [sessions]="store.sessions()"
        [activeId]="store.activeSessionId()"
        (create)="store.startNewSession()"
        (select)="store.selectSession($event)"
        (remove)="store.deleteSession($event)"
      >
        @if (store.modelStatus(); as status) {
          <div class="model" [class.model--error]="!!status.error">
            <span class="model__dot" [class.model__dot--ready]="status.ready"></span>
            <div class="model__text">
              <span class="model__name">{{ status.model }}</span>
              <span class="model__detail">
                @if (status.error) {
                  Failed to load
                } @else if (status.ready) {
                  {{ status.contextSize | number }} token context
                } @else {
                  Loading...
                }
              </span>
            </div>
          </div>
        }
      </app-sidebar>

      <app-chat-panel />
    </div>
  `,
  styleUrl: './app.scss',
})
export class App implements OnInit {
  protected readonly store = inject(ChatStore);

  ngOnInit(): void {
    void this.store.initialise();
  }
}
