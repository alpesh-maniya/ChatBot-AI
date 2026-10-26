/**
 * Small markdown renderer for assistant output.
 *
 * marked and markdown-it both hand you HTML that still needs sanitising before it can
 * go anywhere near [innerHTML], and model output is untrusted. Here every
 * HTML-significant character is escaped first and the transforms run over the escaped
 * text, so no input can produce a tag we didn't generate.
 *
 * Covers fenced code, inline code, bold, italic, headings, ordered and unordered lists,
 * http(s) links, paragraphs and line breaks - about as much markdown as a chat model
 * actually reaches for.
 */

const ESCAPES: Record<string, string> = {
  '&': '&amp;',
  '<': '&lt;',
  '>': '&gt;',
  '"': '&quot;',
  "'": '&#39;',
};

function escapeHtml(text: string): string {
  return text.replace(/[&<>"']/g, (c) => ESCAPES[c]);
}

/** Inline transforms, applied to text that has ALREADY been escaped. */
function renderInline(escaped: string): string {
  return (
    escaped
      // `code`
      .replace(/`([^`]+)`/g, '<code>$1</code>')
      // **bold**
      .replace(/\*\*([^*]+)\*\*/g, '<strong>$1</strong>')
      // *italic* / _italic_ - require a non-word char before to avoid mangling snake_case
      .replace(/(^|[\s(])\*([^*\n]+)\*/g, '$1<em>$2</em>')
      .replace(/(^|[\s(])_([^_\n]+)_/g, '$1<em>$2</em>')
      // [label](url) - http/https only. javascript: and data: URLs are simply not matched.
      .replace(
        /\[([^\]]+)\]\((https?:\/\/[^\s)]+)\)/g,
        '<a href="$2" target="_blank" rel="noopener noreferrer">$1</a>',
      )
  );
}

export function renderMarkdown(source: string): string {
  const out: string[] = [];

  // Fenced code blocks are pulled out first so their contents are never treated as
  // markdown - otherwise an asterisk inside a code sample becomes italics.
  const segments = source.split(/```/);

  segments.forEach((segment, index) => {
    const isCode = index % 2 === 1;

    if (isCode) {
      const newline = segment.indexOf('\n');
      const language = newline >= 0 ? segment.slice(0, newline).trim() : '';
      const body = newline >= 0 ? segment.slice(newline + 1) : segment;
      const cls = language ? ` class="language-${escapeHtml(language)}"` : '';
      out.push(`<pre><code${cls}>${escapeHtml(body.replace(/\n$/, ''))}</code></pre>`);
      return;
    }

    out.push(renderBlocks(segment));
  });

  return out.join('');
}

function renderBlocks(source: string): string {
  const lines = escapeHtml(source).split('\n');
  const out: string[] = [];

  let listType: 'ul' | 'ol' | null = null;
  let paragraph: string[] = [];

  const closeParagraph = () => {
    if (paragraph.length) {
      out.push(`<p>${renderInline(paragraph.join('<br>'))}</p>`);
      paragraph = [];
    }
  };
  const closeList = () => {
    if (listType) {
      out.push(`</${listType}>`);
      listType = null;
    }
  };

  for (const line of lines) {
    const trimmed = line.trim();

    if (!trimmed) {
      closeParagraph();
      closeList();
      continue;
    }

    const heading = /^(#{1,4})\s+(.*)$/.exec(trimmed);
    if (heading) {
      closeParagraph();
      closeList();
      const level = heading[1].length + 1; // h1 is reserved for the page itself
      out.push(`<h${level}>${renderInline(heading[2])}</h${level}>`);
      continue;
    }

    const ordered = /^\d+\.\s+(.*)$/.exec(trimmed);
    const unordered = /^[-*+]\s+(.*)$/.exec(trimmed);

    if (ordered || unordered) {
      closeParagraph();
      const wanted = ordered ? 'ol' : 'ul';
      if (listType !== wanted) {
        closeList();
        out.push(`<${wanted}>`);
        listType = wanted;
      }
      out.push(`<li>${renderInline((ordered ?? unordered)![1])}</li>`);
      continue;
    }

    closeList();
    paragraph.push(trimmed);
  }

  closeParagraph();
  closeList();
  return out.join('');
}
