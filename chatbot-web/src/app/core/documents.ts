import type { DocumentInfo } from './chat.models';

// Display rules for a report, shared by the sidebar, the welcome cards and the source
// chips so all three describe it the same way.

export function isPending(doc: DocumentInfo): boolean {
  return doc.status === 'Queued' || doc.status === 'Processing';
}

/** "82 pages", "Indexing 45%", "Waiting to index", "Failed". */
export function documentMeta(doc: DocumentInfo): string {
  switch (doc.status) {
    case 'Ready':
      return doc.pdfPageCount ? `${doc.pdfPageCount} page${doc.pdfPageCount === 1 ? '' : 's'}` : 'Ready';
    case 'Processing':
      return doc.progress != null ? `Indexing ${Math.round(doc.progress * 100)}%` : 'Indexing...';
    case 'Queued':
      return 'Waiting to index';
    default:
      return 'Could not be indexed';
  }
}

export function progressPercent(doc: DocumentInfo): number {
  if (doc.status === 'Queued') return 2;
  return Math.max(2, Math.round((doc.progress ?? 0) * 100));
}

/** "Global Economic Prospects, June 2026" -> "Global Economic Prospects": short enough for a chip. */
export function shortTitle(title: string): string {
  const cut = title.search(/\s*[,(]/);
  return cut > 0 ? title.slice(0, cut) : title;
}
