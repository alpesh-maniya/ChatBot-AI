import { ChangeDetectionStrategy, Component, input, output } from '@angular/core';
import { DatePipe } from '@angular/common';
import type { SessionSummary } from '../../core/chat.models';

// Conversation list. Each row is one server-side session; "New chat" makes an id with
// no history behind it.
@Component({
  selector: 'app-sidebar',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [DatePipe],
  template: `
    <aside class="sidebar">
      <button type="button" class="sidebar__new" (click)="create.emit()">
        <svg viewBox="0 0 24 24" width="16" height="16" aria-hidden="true">
          <path d="M12 5v14M5 12h14" stroke="currentColor" stroke-width="2" stroke-linecap="round" />
        </svg>
        New chat
      </button>

      <nav class="sidebar__list" aria-label="Conversations">
        @for (session of sessions(); track session.id) {
          <div class="sidebar__item" [class.sidebar__item--active]="session.id === activeId()">
            <button type="button" class="sidebar__link" (click)="select.emit(session.id)">
              <span class="sidebar__title">{{ session.title ?? 'New conversation' }}</span>
              <span class="sidebar__meta">
                {{ session.messageCount }} messages &middot;
                {{ session.lastActivityAt | date: 'shortTime' }}
              </span>
            </button>

            <button
              type="button"
              class="sidebar__delete"
              (click)="remove.emit(session.id)"
              [attr.aria-label]="'Delete ' + (session.title ?? 'conversation')"
            >
              <svg viewBox="0 0 24 24" width="14" height="14" aria-hidden="true">
                <path d="M6 6l12 12M18 6L6 18" stroke="currentColor" stroke-width="2" stroke-linecap="round" />
              </svg>
            </button>
          </div>
        } @empty {
          <p class="sidebar__empty">No conversations yet.</p>
        }
      </nav>

      <footer class="sidebar__footer">
        <ng-content />
      </footer>
    </aside>
  `,
  styleUrl: './sidebar.scss',
})
export class SidebarComponent {
  readonly sessions = input.required<SessionSummary[]>();
  readonly activeId = input<string | null>(null);

  readonly create = output<void>();
  readonly select = output<string>();
  readonly remove = output<string>();
}
