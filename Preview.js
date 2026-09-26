// Preview side of the editor <-> preview scroll sync (PreviewSync.cs is the editor side).
//
// Every rendered block carries the source line it starts on (data-line) and the editor reports where
// each of those lines sits in its own scroll space. Pairing the two gives anchor points; positions in
// between are interpolated, so the panes track each other smoothly instead of jumping block by block.
//
// Whichever pane the user last scrolled or typed in leads and the other follows. The follower never
// reports its own programmatic scrolling back, so the two can't fight.
(() => {
  'use strict';
  const root = document.scrollingElement || document.documentElement;
  const content = document.getElementById('content');
  const host = window.chrome && window.chrome.webview;

  let ed = null;           // editor scroll state: {top, max, vh}
  let tops = new Map();    // source line -> its y in the editor
  let total = 0;           // editor content height when `tops` was measured
  let anchors = [];        // [{e, p}]: editor y increasing, preview y never decreasing
  let lead = 'editor';
  let offset = 0;          // how far the preview sits from the pure mapping
  let ownTop = -1;         // scrollTop we set ourselves, so its scroll event is ignored
  let lastTop = 0;
  let moved = 0;           // editor movement not followed yet
  let version = 0;
  let frame = 0;

  const clamp = (v, lo, hi) => Math.min(hi, Math.max(lo, v));
  const maxScroll = () => Math.max(0, root.scrollHeight - root.clientHeight);
  const ready = () => ed !== null && anchors.length > 1;

  // While the editor leads the page is positioned from the editor, and browser scroll anchoring would
  // fight that; while the preview leads, anchoring keeps the reading spot steady as images load.
  root.style.overflowAnchor = 'none';

  // Each block gives two anchors: its top (where its first line is) and, once everything inside it is in,
  // its bottom (where the line after it starts) - so text lines up with text and blank lines with gaps.
  function buildAnchors() {
    const scrollY = root.scrollTop;
    let last = { e: 0, p: 0 };
    anchors = [last];
    const add = (e, p) => {   // skips anything out of source order (e.g. footnotes, rendered last)
      if (e !== undefined && e > last.e && p >= last.p) anchors.push(last = { e, p });
    };
    const open = [];          // blocks whose bottom is still to come, innermost last
    const closeUntil = node => {
      while (open.length && !(node && open[open.length - 1].node.contains(node))) {
        const { box, end } = open.pop();
        add(tops.get(end), box.getBoundingClientRect().bottom + scrollY);
      }
    };
    for (const node of content.querySelectorAll('[data-line]')) {
      closeUntil(node);
      // Code blocks carry the markers on <code>; the <pre> box is what lines up with the fences.
      const box = node.tagName === 'CODE' && node.parentElement.tagName === 'PRE' ? node.parentElement : node;
      add(tops.get(Number(node.getAttribute('data-line'))), box.getBoundingClientRect().top + scrollY);
      if (node.hasAttribute('data-end')) open.push({ node, box, end: Number(node.getAttribute('data-end')) });
    }
    closeUntil(null);
    // The end of the document lines up with the end of the page (padding included), so both panes reach
    // their bottom together; a block ending on the last line gives way to that.
    while (anchors.length > 1 && last.e >= total) { anchors.pop(); last = anchors[anchors.length - 1]; }
    if (total > last.e) anchors.push({ e: total, p: root.scrollHeight });
  }

  // Piecewise-linear lookup through the anchors, from one coordinate ('e' or 'p') to the other.
  function lookup(from, to, x) {
    const a = anchors, n = a.length - 1;
    if (x <= a[0][from]) return a[0][to];
    if (x >= a[n][from]) return a[n][to];
    let lo = 0, hi = n;
    while (hi - lo > 1) {
      const mid = (lo + hi) >> 1;
      if (a[mid][from] <= x) lo = mid; else hi = mid;
    }
    const A = a[lo], B = a[hi], span = B[from] - A[from];
    return span > 0 ? A[to] + (x - A[from]) / span * (B[to] - A[to]) : A[to];
  }

  // Preview scroll position for an editor scroll position. The point the panes line up on slides from
  // the top of the view at the start of the document to the bottom at the end, so both panes reach
  // their top and their bottom together - no snap at either end.
  function previewTopFor(editorTop) {
    const f = ed.max > 0 ? clamp(editorTop / ed.max, 0, 1) : 0;
    return clamp(lookup('e', 'p', editorTop + f * ed.vh) - f * root.clientHeight, 0, maxScroll());
  }

  // Inverse of previewTopFor, for when the preview leads.
  function editorTopFor(previewTop) {
    let lo = 0, hi = ed.max;
    while (hi - lo > 0.25) {
      const mid = (lo + hi) / 2;
      if (previewTopFor(mid) < previewTop) lo = mid; else hi = mid;
    }
    return (lo + hi) / 2;
  }

  function setTop(t) {
    if (Math.abs(root.scrollTop - t) >= 0.5) root.scrollTop = t;
    ownTop = lastTop = root.scrollTop;
  }

  // Editor leads: put the preview where the editor is. When following a scroll (dir = its direction),
  // never step against it: across source that renders to nothing (comments, link definitions) the
  // mapping can dip slightly backwards, and holding still there reads better than creeping in reverse.
  function follow(dir = 0) {
    if (!ready()) return;
    const base = previewTopFor(ed.top);
    let t = clamp(base + offset, 0, maxScroll());
    if (dir * (t - lastTop) < 0) t = lastTop;
    offset = t - base; // never hold on to an offset the view can't show
    setTop(t);
  }

  // Preview leads: move the editor to match.
  function moveEditor() {
    if (!ready() || !host) return;
    const top = editorTopFor(root.scrollTop - offset);
    if (Math.abs(top - ed.top) >= 0.5) host.postMessage({ t: 'scroll', top });
  }

  // An offset (from revealing typed text, a handover, a held step or a stray scroll) eases away as the
  // leading pane moves from `from` to `to` (preview pixels) instead of snapping back: by half the distance
  // moved, and in proportion to the room left toward the end being approached, so both panes still reach
  // that end together. Either way the follower never reverses direction.
  function decay(from, to) {
    const max = maxScroll();
    from = clamp(from, 0, max);
    to = clamp(to, 0, max);
    if (!offset || from === to) return;
    const room = to < from ? to / from : (max - to) / (max - from);
    const left = Math.min(Math.abs(offset) - Math.abs(to - from) / 2, Math.abs(offset) * room);
    offset = left > 0 ? Math.sign(offset) * left : 0;
  }

  // After an edit, keep what's being typed on screen. `r` is the caret's line in editor pixels
  // ({top, bottom}); its bottom is the document end when only blank lines follow the caret.
  function reveal(r) {
    const y0 = lookup('e', 'p', r.top), y1 = lookup('e', 'p', r.bottom);
    const vh = root.clientHeight, margin = Math.min(40, vh / 10);
    const base = previewTopFor(ed.top);
    let t = clamp(base + offset, 0, maxScroll());
    if (y1 > t + vh - margin || y1 - y0 > vh - 2 * margin) t = y1 - vh + margin;
    else if (y0 < t + margin) t = y0 - margin;
    offset = clamp(t, 0, maxScroll()) - base;
  }

  function schedule() {
    if (frame) return;
    frame = requestAnimationFrame(() => {
      frame = 0;
      const dir = Math.sign(moved);
      moved = 0;
      if (lead === 'editor') follow(dir);
    });
  }

  function takeEditorState(m) {
    ed = { top: m.top, max: m.max, vh: m.vh };
    if (!m.anchorTops) return false;
    tops = new Map(m.anchorLines.map((line, i) => [line, m.anchorTops[i]]));
    total = m.total;
    return true;
  }

  function onContent(m) {
    if (m.v <= version) return; // an older update that arrived late
    version = m.v;
    content.innerHTML = m.html;
    takeEditorState(m.editor);
    buildAnchors();
    moved = 0;
    // Same task as the content swap, so the new content is painted already in place.
    if (lead === 'editor') {
      const r = m.reveal;
      if (r && Number.isFinite(r.top) && Number.isFinite(r.bottom)) reveal(r);
      follow();
    } else moveEditor();
  }

  function onEditor(m) {
    const oldTop = ed ? ed.top : null;
    if (takeEditorState(m)) buildAnchors();
    if (lead !== 'editor') {
      if (m.anchorTops) moveEditor(); // the editor re-wrapped while the preview leads: keep it matched
      return;
    }
    if (oldTop !== null && ready()) {
      decay(previewTopFor(oldTop), previewTopFor(ed.top)); // both measured on the current anchors
      moved += ed.top - oldTop;
    }
    schedule();
  }

  // Either pane taking over carries on from where the preview is, so nothing jumps. That's the last
  // position this page processed: by the time a wheel event arrives, the compositor may already have
  // scrolled, and that movement is the user's to pass on to the editor, not an offset to keep.
  function takeLead(pane) {
    if (lead === pane) return;
    lead = pane;
    root.style.overflowAnchor = pane === 'editor' ? 'none' : 'auto';
    moved = 0;
    if (ready()) offset = lastTop - previewTopFor(ed.top);
    if (pane === 'preview' && host) host.postMessage({ t: 'lead' });
  }

  function relayout() {
    if (!ed) return;
    buildAnchors();
    if (lead === 'editor') follow(); else moveEditor();
  }

  addEventListener('scroll', () => {
    const t = root.scrollTop, from = lastTop;
    lastTop = t;
    if (Math.abs(t - ownTop) < 1) return; // our own scroll landing
    ownTop = -1;
    if (!ready()) return;
    if (lead === 'editor') { offset = t - previewTopFor(ed.top); return; } // stray scroll: keep it, no jump later
    decay(from, t);
    moveEditor();
  }, { passive: true });

  for (const type of ['wheel', 'mousedown', 'touchstart', 'keydown'])
    addEventListener(type, () => takeLead('preview'), { capture: true, passive: true });

  // Images loading, fonts, window or splitter resizes: re-measure and re-align.
  new ResizeObserver(relayout).observe(content);
  addEventListener('resize', relayout);

  // The editor jumped somewhere on request (find): keep that line on screen here too. The message
  // carries the editor's new position, so the preview moves once, straight to it.
  function onReveal(m) {
    takeLead('editor');
    takeEditorState(m.editor);
    moved = 0;
    const r = m.band;
    if (!ready() || !r || !Number.isFinite(r.top) || !Number.isFinite(r.bottom)) return;
    reveal(r);
    follow();
  }

  function receive(m) {
    if (m.t === 'content') onContent(m);
    else if (m.t === 'editor') onEditor(m);
    else if (m.t === 'reveal') onReveal(m);
    else if (m.t === 'lead') takeLead('editor');
  }
  if (host) host.addEventListener('message', e => receive(e.data));
  window.mdpad = { receive };
})();
