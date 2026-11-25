// Pure helpers the screens share (Node-testable: no DOM).

// How many screens a history move pops: back from depth `from` to `to` pops that many, keeping the root; forward pops none.
export const screensToPop = (stackLen, from, to) => Math.max(0, Math.min(stackLen - 1, from - to));

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
