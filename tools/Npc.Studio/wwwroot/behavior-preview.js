// T24 · H20·H24 — 동작 미리보기 재생기. 정적 파일이고 빌드가 없다.
// 재생은 클라이언트에서 한다: Blazor Server 가 프레임마다 SignalR 을 타면 안 된다.
// 서버는 키프레임을 한 번 주고, 여기서 requestAnimationFrame 으로 움직인다.
window.behaviorPreview = (function () {
  const views = new Map();
  let running = false;

  function find(id) { return views.get(id); }

  function reducedMotion() {
    return window.matchMedia && window.matchMedia('(prefers-reduced-motion: reduce)').matches;
  }

  function positionAt(frames, t) {
    if (!frames.length) return { x: 0, z: 0, seg: -1, state: 'Wait' };
    let lo = 0, hi = frames.length - 1;
    if (t <= frames[0].t) return frames[0];
    if (t >= frames[hi].t) return frames[hi];
    while (lo < hi - 1) {
      const mid = (lo + hi) >> 1;
      if (frames[mid].t <= t) lo = mid; else hi = mid;
    }
    const a = frames[lo], b = frames[hi];
    const span = b.t - a.t;
    const k = span <= 0 ? 0 : (t - a.t) / span;
    return { x: a.x + (b.x - a.x) * k, z: a.z + (b.z - a.z) * k, seg: a.seg, state: a.state, caption: a.caption };
  }

  function figureOf(view) { return view.root.querySelector('[data-npc-figure]'); }

  function place(view, x, z, state) {
    const g = figureOf(view);
    if (!g) return;
    g.setAttribute('transform', 'translate(' + x.toFixed(2) + ' ' + z.toFixed(2) + ')');
    g.setAttribute('data-state', state || 'Wait');
  }

  function render(view) {
    // H20 — 돌발 반응은 뛰어간다. dash 중에는 키프레임 위치가 인형을 되돌리면 안 된다.
    if (!view.dash) {
      const p = positionAt(view.frames, view.time);
      place(view, p.x, p.z, p.state);

      view.root.querySelectorAll('[data-seg]').forEach(function (el) {
        el.classList.toggle('active', Number(el.getAttribute('data-seg')) === p.seg);
      });

      if (p.seg !== view.segment) {
        view.segment = p.seg;
        if (view.dotnet) {
          // 회로가 이미 끊겼으면 여기서 터진다 — 재생 루프까지 같이 죽이지 않는다.
          try { view.dotnet.invokeMethodAsync('OnSegment', p.seg).catch(function () {}); }
          catch (e) { view.dotnet = null; }
        }
      }
    }

    const clock = view.root.querySelector('[data-clock]');
    if (clock) {
      const total = (view.startClock + view.time) % 86400;
      const h = Math.floor(total / 3600), m = Math.floor((total % 3600) / 60);
      clock.textContent = String(h).padStart(2, '0') + ':' + String(m).padStart(2, '0');
    }
    const scrub = view.root.querySelector('[data-scrubber]');
    if (scrub && !view.dragging) scrub.value = String(Math.round(view.time));
  }

  // 탭이 숨으면 브라우저가 rAF 를 멈춘다. 돌아왔을 때 그동안의 간격을 한 번에 더하면
  // 인형이 순간이동하므로 한 프레임의 실시간 간격을 0.25초로 자른다.
  const MaxFrameSeconds = 0.25;

  function step(now) {
    let alive = 0;

    views.forEach(function (view) {
      if (view.dash) {
        alive++;
        const k = Math.min(1, (now - view.dash.start) / view.dash.ms);
        const e = k * k * (3 - 2 * k); // ease-in-out — 걸음이 갑자기 서지 않는다
        place(view, view.dash.fromX + (view.dash.toX - view.dash.fromX) * e,
                    view.dash.fromZ + (view.dash.toZ - view.dash.fromZ) * e, 'Move');
        if (k >= 1) view.dash = null;
        return;
      }

      if (!view.playing) { view.last = now; return; }
      alive++;
      const dt = view.last ? Math.min((now - view.last) / 1000, MaxFrameSeconds) : 0;
      view.last = now;
      view.time += dt * view.speed;
      if (view.time >= view.duration) view.time = view.time % Math.max(1, view.duration);
      render(view);
    });

    // 재생 중인 뷰가 없으면 루프를 세운다 — 빈 rAF 가 배터리를 먹을 이유가 없다.
    if (alive > 0) { requestAnimationFrame(step); } else { running = false; }
  }

  function pump() {
    if (!running) { running = true; requestAnimationFrame(step); }
  }

  function bindScrubber(view) {
    const scrub = view.root.querySelector('[data-scrubber]');
    if (!scrub || scrub.dataset.bound === '1') return;
    scrub.dataset.bound = '1';

    // H24 — `dragging` 을 세우는 리스너가 없어서, 재생 중에 손잡이를 잡으면 값이 튀었다.
    const down = function () { view.dragging = true; };
    const up = function () { view.dragging = false; };
    scrub.addEventListener('pointerdown', down);
    scrub.addEventListener('pointerup', up);
    scrub.addEventListener('pointercancel', up);
    scrub.addEventListener('input', function () {
      view.time = Number(scrub.value) || 0;
      render(view);
    });
  }

  return {
    load: function (id, payload, dotnet) {
      const root = document.getElementById(id);
      if (!root) return;
      const data = typeof payload === 'string' ? JSON.parse(payload) : payload;
      const existing = views.get(id);
      const view = existing || { time: 0, speed: 600, playing: false, segment: -2, dragging: false, dash: null };
      view.root = root;
      view.frames = data.frames || [];
      view.duration = data.duration || 86400;
      view.startClock = data.startClock || 0;
      if (dotnet) view.dotnet = dotnet;
      if (view.time > view.duration) view.time = 0;
      views.set(id, view);
      bindScrubber(view);
      render(view);
      pump();
    },
    play: function (id) { const v = find(id); if (v) { v.playing = true; v.last = 0; pump(); } },
    pause: function (id) { const v = find(id); if (v) { v.playing = false; } },
    toggle: function (id) { const v = find(id); if (v) { v.playing = !v.playing; v.last = 0; pump(); } return !!(v && v.playing); },
    speed: function (id, value) { const v = find(id); if (v) v.speed = value; },
    seek: function (id, seconds) { const v = find(id); if (v) { v.dash = null; v.time = seconds; render(v); } },
    // H20 — 돌발 반응은 순간이동이 아니라 2초 동안 뛰어간다. 문구가 사실이 된다.
    dash: function (id, x, z, ms) {
      const v = find(id);
      if (!v) return;
      const g = figureOf(v);
      const now = positionAt(v.frames, v.time);
      const fromX = now.x, fromZ = now.z;
      if (!g || reducedMotion() || !ms) { place(v, x, z, 'Move'); return; }
      v.playing = false;
      v.dash = { start: performance.now(), ms: ms, fromX: fromX, fromZ: fromZ, toX: x, toZ: z };
      pump();
    },
    dispose: function (id) { views.delete(id); }
  };
})();
