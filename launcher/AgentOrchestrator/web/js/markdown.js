// Markdown for replies. parseMarkdown is pure (Node tests it); renderMarkdown builds elements and text nodes only.
// Blocks: heading {level, runs}, paragraph {runs}, list {ordered, items: runs[]}, code {lang, text}, table {head, rows}, quote {runs}.
// Runs: {t: text|bold|italic|code|link, text, href?}; links are http/https only, anything else stays plain text.
import { h } from './h.js';

// `_` emphasis only at word edges, as CommonMark (my_file_name.py stays text); `*` may sit inside a word.
const INLINE = /(`[^`]+`)|(\*\*[^*]+\*\*|(?<![\p{L}\p{N}_])__[^_]+__(?![\p{L}\p{N}_]))|(\*[^*\s][^*]*\*|(?<![\p{L}\p{N}_])_[^_\s][^_]*_(?![\p{L}\p{N}_]))|(\[[^\]]+\]\((https?:\/\/[^\s)]+)\))/u;

export function parseInline(s) {
  const runs = [];
  const text = t => {
    if (!t) return;
    const last = runs[runs.length - 1];
    if (last && last.t === 'text') last.text += t; else runs.push({ t: 'text', text: t });
  };
  while (s) {
    const m = INLINE.exec(s);
    if (!m) { text(s); break; }
    text(s.slice(0, m.index));
    const [all, code, bold, italic, link, href] = m;
    if (code) runs.push({ t: 'code', text: code.slice(1, -1) });
    else if (bold) runs.push({ t: 'bold', text: bold.slice(2, -2) });
    else if (italic) runs.push({ t: 'italic', text: italic.slice(1, -1) });
    else if (link) runs.push({ t: 'link', text: link.slice(1, link.indexOf('](')), href });
    s = s.slice(m.index + all.length);
  }
  return runs;
}

const cells = line => line.trim().replace(/^\||\|$/g, '').split('|').map(c => parseInline(c.trim()));
const isRule = line => /^\s*\|?\s*:?-{3,}:?\s*(\|\s*:?-{3,}:?\s*)*\|?\s*$/.test(line);
const BULLET = /^\s*[-*+]\s+(.*)$/, NUMBER = /^\s*\d+[.)]\s+(.*)$/;
const STARTS_BLOCK = /^\s*(```|#{1,3}\s|>)/;

export function parseMarkdown(src) {
  const lines = String(src ?? '').replace(/\r\n?/g, '\n').split('\n');
  const blocks = [];
  let i = 0, m;
  while (i < lines.length) {
    const line = lines[i];
    if (!line.trim()) { i++; continue; }
    if ((m = /^\s*```\s*([\w+-]*)/.exec(line))) {
      const body = [];
      for (i++; i < lines.length && !/^\s*```/.test(lines[i]); i++) body.push(lines[i]);
      i++;
      blocks.push({ type: 'code', lang: m[1], text: body.join('\n') });
    } else if ((m = /^(#{1,3})\s+(.*)$/.exec(line))) {
      blocks.push({ type: 'heading', level: m[1].length, runs: parseInline(m[2].trim()) });
      i++;
    } else if (line.includes('|') && i + 1 < lines.length && isRule(lines[i + 1])) {
      const head = cells(line), rows = [];
      for (i += 2; i < lines.length && lines[i].includes('|') && lines[i].trim(); i++) rows.push(cells(lines[i]));
      blocks.push({ type: 'table', head, rows });
    } else if (/^\s*>/.test(line)) {
      const body = [];
      for (; i < lines.length && /^\s*>/.test(lines[i]); i++) body.push(lines[i].replace(/^\s*>\s?/, ''));
      blocks.push({ type: 'quote', runs: parseInline(body.join(' ')) });
    } else if (BULLET.test(line) || NUMBER.test(line)) {
      const re = BULLET.test(line) ? BULLET : NUMBER, items = [];
      for (; i < lines.length && (m = re.exec(lines[i])); i++) items.push(parseInline(m[1]));
      blocks.push({ type: 'list', ordered: re === NUMBER, items });
    } else {
      const body = [];
      for (; i < lines.length && lines[i].trim() && !STARTS_BLOCK.test(lines[i]) && !BULLET.test(lines[i]) && !NUMBER.test(lines[i]); i++)
        body.push(lines[i].trim());
      blocks.push({ type: 'paragraph', runs: parseInline(body.join(' ')) });
    }
  }
  return blocks;
}

const TAG = { bold: 'strong', italic: 'em', code: 'code' };
const runsTo = runs => runs.map(r =>
  r.t === 'text' ? r.text
  : r.t === 'link' ? h('a', { href: r.href, target: '_blank', rel: 'noopener noreferrer' }, r.text)
  : h(TAG[r.t], null, r.text));

export function renderMarkdown(src) {
  const frag = document.createDocumentFragment();
  for (const b of parseMarkdown(src)) {
    if (b.type === 'heading') frag.append(h('h' + b.level, null, runsTo(b.runs)));
    else if (b.type === 'paragraph') frag.append(h('p', null, runsTo(b.runs)));
    else if (b.type === 'quote') frag.append(h('blockquote', null, h('p', null, runsTo(b.runs))));
    else if (b.type === 'list') frag.append(h(b.ordered ? 'ol' : 'ul', null, b.items.map(it => h('li', null, runsTo(it)))));
    else if (b.type === 'code') frag.append(h('pre', { 'data-lang': b.lang || null }, h('code', null, b.text)));
    else if (b.type === 'table') frag.append(h('div', { class: 'tablewrap' }, h('table', null,
      h('thead', null, h('tr', null, b.head.map(c => h('th', null, runsTo(c))))),
      h('tbody', null, b.rows.map(r => h('tr', null, r.map(c => h('td', null, runsTo(c)))))))));
  }
  return frag;
}
