// The drawer's PC list: this PC first and ticked, then the owner's other PCs with their online state; tap one to switch.
import { api } from './api.js';
import { fail, toast, closeOverlays, usePc, rememberPcs } from './main.js';

export const list = () => api.get('/api/pcs').then(rememberPcs);

// Switches in place (main.js usePc); the PC the app talks to now (self) just closes the drawer.
export function switchTo(pc) {
  if (pc.self) { closeOverlays(); return; }
  if (!pc.online) { toast(`${pc.pc} is offline`); return; }
  usePc(pc.dnsName, pc.pc);
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
