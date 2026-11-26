// Pure helpers the screens share (Node-testable: no DOM).

// The screen stack ({name, params} each, Chats at the bottom), kept in the tab's `history` so the phone's back gesture and the
// Back button go back one screen. Each entry's state is the stack it shows. Under them sits the anchor (state null): landing
// there is a back at the root, which shows Chats and pushes it again, so the gesture never leaves the app. start() makes the
// page's own entry the anchor, so a load or reload starts clean at Chats. A tab switch or new chat replaces the entry
// (back from it goes to the screen before, or Chats); pop(state) is popstate's.
export function makeRouter(history) {
  const root = () => [{ name: 'chats', params: {} }];
  let stack;
  const set = (s, how) => { stack = s; if (how) history[how]({ stack }, ''); };
  return {
    get stack() { return stack; },
    start() { history.replaceState(null, ''); set(root(), 'pushState'); },
    go(screen) { set([...stack, screen], 'pushState'); },
    reset(s) { set(s, 'replaceState'); },
    pop(state) { if (state?.stack) set(state.stack); else set(root(), 'pushState'); },
  };
}

// A long press on `el` (held `ms` with the primary button or a finger, moving under `slop` px) or a right-click calls fn(); the
// click that ends a long press is swallowed, so the row under it does not also open.
export function longPress(el, fn, ms = 500, slop = 10) {
  let timer = 0, at = null, fired = false;
  const cancel = () => { clearTimeout(timer); at = null; };
  const fire = () => { cancel(); fired = true; fn(); };
  el.addEventListener('pointerdown', e => {
    fired = false;
    if (e.button !== 0) return;
    at = [e.clientX, e.clientY];
    clearTimeout(timer);
    timer = setTimeout(fire, ms);
  });
  el.addEventListener('pointermove', e => { if (at && Math.hypot(e.clientX - at[0], e.clientY - at[1]) > slop) cancel(); });
  for (const end of ['pointerup', 'pointercancel', 'pointerleave']) el.addEventListener(end, cancel);
  el.addEventListener('contextmenu', e => { e.preventDefault(); if (!fired) fire(); });
  el.addEventListener('click', e => { if (fired) { fired = false; e.preventDefault(); e.stopPropagation(); } }, true);
}

// "3 min ago", "yesterday", "2 days ago", "last week", then the date: the list rows' time, as the concept writes it.
export function ago(when, now = new Date()) {
  const d = new Date(when), mins = Math.floor((now - d) / 60000);
  if (mins < 1) return 'just now';
  if (mins < 60) return `${mins} min ago`;
  const day = x => new Date(x.getFullYear(), x.getMonth(), x.getDate());
  const days = Math.round((day(now) - day(d)) / 864e5);
  if (days === 0) return `${Math.floor(mins / 60)} h ago`;
  if (days === 1) return 'yesterday';
  if (days < 7) return `${days} days ago`;
  if (days < 14) return 'last week';
  return d.toLocaleDateString('en-US', { month: 'short', day: 'numeric' });
}
