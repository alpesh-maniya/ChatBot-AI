import { ChangeDetectionStrategy, Component, computed, inject, OnInit } from '@angular/core';
import { DecimalPipe } from '@angular/common';
import { ChatStore } from './core/chat-store';
import type { SessionSummary } from './core/chat.models';
import { SidebarComponent } from './components/sidebar/sidebar';
import { ChatPanelComponent } from './components/chat-panel/chat-panel';
import { SourcePanelComponent } from './components/source-panel/source-panel';
import { DocumentListComponent } from './components/document-list/document-list';

// Shell: conversations and the three reports on the left, the open conversation on the
// right, the citation drawer over both.
@Component({
  selector: 'app-root',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [DecimalPipe, SidebarComponent, ChatPanelComponent, SourcePanelComponent, DocumentListComponent],
  template: `
    <div class="shell">
      <app-sidebar
        [sessions]="store.visibleSessions()"
        [activeId]="store.activeSessionId()"
        [scopeOf]="scopeOf"
        (create)="store.startNewSession()"
        (select)="store.selectSession($event)"
        (remove)="store.deleteSession($event)"
      >
        @if (store.modelStatus()?.ragEnabled) {
          <app-document-list
            sidebar-documents
            [documents]="store.documents()"
            [activeIds]="store.generalChat() ? [] : store.activeDocumentIds()"
          />
        }

        @if (store.modelStatus(); as status) {
          <div class="model" [class.model--error]="!!status.error || !!status.indexError">
            <span class="model__dot" [class.model__dot--ready]="status.ready && (!status.ragEnabled || status.indexReady)"></span>
            <div class="model__text">
              <span class="model__name">{{ status.model }}</span>
              <span class="model__detail">
                @if (status.error) {
                  Failed to load
                } @else if (status.indexError) {
                  Reports failed to load
                } @else if (!status.ready || (status.ragEnabled && !status.indexReady)) {
                  Loading...
                } @else if (pending() > 0) {
                  Indexing {{ pending() }} report{{ pending() === 1 ? '' : 's' }}...
                } @else {
                  {{ status.contextSize | number }} token context · runs on this machine
                }
              </span>
            </div>
          </div>
        }
      </app-sidebar>

      <app-chat-panel />
      <app-source-panel />
    </div>
  `,
  styleUrl: './app.scss',
})
export class App implements OnInit {
  protected readonly store = inject(ChatStore);

  protected readonly pending = computed(
    () => this.store.documents().filter((d) => d.status === 'Queued' || d.status === 'Processing').length,
  );

  protected readonly scopeOf = (s: SessionSummary) => this.store.scopeLabel(s.documentIds, s.useDocuments);

  ngOnInit(): void {
    void this.store.initialise();
  }
}
