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
      <div class="sidebar__brand">
        <span class="sidebar__logo" aria-hidden="true">
          <svg viewBox="0 0 24 24" width="16" height="16">
            <path
              d="M4 5.5A2.5 2.5 0 0 1 6.5 3h11A2.5 2.5 0 0 1 20 5.5v8a2.5 2.5 0 0 1-2.5 2.5H10l-4.5 4v-4A2.5 2.5 0 0 1 4 13.5v-8Z"
              fill="currentColor"
            />
          </svg>
        </span>
        <span class="sidebar__names">
          <span class="sidebar__name">ChatBot</span>
          <span class="sidebar__tagline">Chat, or ask your reports</span>
        </span>
      </div>

      <button type="button" class="sidebar__new" (click)="create.emit()">
        <svg viewBox="0 0 24 24" width="16" height="16" aria-hidden="true">
          <path d="M12 5v14M5 12h14" stroke="currentColor" stroke-width="2" stroke-linecap="round" />
        </svg>
        New chat
      </button>

      <p class="sidebar__heading">Chats</p>

      <nav class="sidebar__list" aria-label="Conversations">
        @for (session of sessions(); track session.id) {
          <div class="sidebar__item" [class.sidebar__item--active]="session.id === activeId()">
            <button type="button" class="sidebar__link" (click)="select.emit(session.id)">
              <span class="sidebar__title">{{ session.title ?? 'New conversation' }}</span>
              <span class="sidebar__meta">
                {{ scopeOf()(session) }} &middot;
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

      <div class="sidebar__documents">
        <ng-content select="[sidebar-documents]" />
      </div>

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
  /** "General chat", "All 3 reports"... for the line under each chat's title. */
  readonly scopeOf = input<(session: SessionSummary) => string>((s) => `${s.messageCount} messages`);

  readonly create = output<void>();
  readonly select = output<string>();
  readonly remove = output<string>();
}
