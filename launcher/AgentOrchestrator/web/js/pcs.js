// The drawer's PC list: this PC first and ticked, then the owner's other PCs with their online state; tap one to switch.
import { api } from './api.js';
import { fail, toast, closeOverlays } from './main.js';

export const list = () => api.get('/api/pcs');

// Step 1 opens the other PC's own page (Task 8 switches in place) and remembers it.
export function switchTo(pc) {
  if (pc.self) { closeOverlays(); return; }
  if (!pc.online) { toast(`${pc.pc} is offline`); return; }
  const host = pc.dnsName.replace(/\.$/, '');
  localStorage.setItem('ao.lastPc', host);
  location.href = `https://${host}/`;
}

async function draw() {
  let pcs;
  try { pcs = await list(); } catch (err) { fail(err); return; }
  const tpl = document.getElementById('tpl-pc').content.firstElementChild;
  document.getElementById('pcList').replaceChildren(...pcs.sort((a, b) => b.self - a.self).map(pc => {
    const el = tpl.cloneNode(true);
    el.querySelector('.pc-name').textContent = pc.pc;
    el.querySelector('.pc-self').toggleAttribute('hidden', !pc.self); // an <svg>: no .hidden property
    el.querySelector('.dot').classList.toggle('off', !pc.online);
    el.setAttribute('aria-label', `${pc.pc}, ${pc.self ? 'this PC' : pc.online ? 'online' : 'offline'}`);
    el.addEventListener('click', () => switchTo(pc));
    return el;
  }));
}

// Fresh online states each time the drawer opens.
document.addEventListener('click', e => { if (e.target.closest('[data-drawer]')) draw(); });
