// T24 — 동작 미리보기 재생기. 정적 파일이고 빌드가 없다.
// 재생은 클라이언트에서 한다: Blazor Server 가 프레임마다 SignalR 을 타면 안 된다.
// 서버는 키프레임을 한 번 주고, 여기서 requestAnimationFrame 으로 움직인다.
window.behaviorPreview = (function () {
  const views = new Map();

  function find(id) { return views.get(id); }

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

  function render(view) {
    const p = positionAt(view.frames, view.time);
    const g = view.root.querySelector('[data-npc-figure]');
    if (g) {
      g.setAttribute('transform', 'translate(' + p.x.toFixed(2) + ' ' + p.z.toFixed(2) + ')');
      g.setAttribute('data-state', p.state || 'Wait');
    }
    const clock = view.root.querySelector('[data-clock]');
    if (clock) {
      const total = (view.startClock + view.time) % 86400;
      const h = Math.floor(total / 3600), m = Math.floor((total % 3600) / 60);
      clock.textContent = String(h).padStart(2, '0') + ':' + String(m).padStart(2, '0');
    }
    const scrub = view.root.querySelector('[data-scrubber]');
    if (scrub && !view.dragging) scrub.value = String(Math.round(view.time));

    view.root.querySelectorAll('[data-seg]').forEach(function (el) {
      el.classList.toggle('active', Number(el.getAttribute('data-seg')) === p.seg);
    });

    if (p.seg !== view.segment) {
      view.segment = p.seg;
      if (view.dotnet) view.dotnet.invokeMethodAsync('OnSegment', p.seg);
    }
  }

  // 탭이 숨으면 브라우저가 rAF 를 멈춘다. 돌아왔을 때 그동안의 간격을 한 번에 더하면
  // 인형이 순간이동하므로 한 프레임의 실시간 간격을 0.25초로 자른다.
  const MaxFrameSeconds = 0.25;

  function step(now) {
    views.forEach(function (view) {
      if (!view.playing) { view.last = now; return; }
      const dt = view.last ? Math.min((now - view.last) / 1000, MaxFrameSeconds) : 0;
      view.last = now;
      view.time += dt * view.speed;
      if (view.time >= view.duration) view.time = view.time % Math.max(1, view.duration);
      render(view);
    });
    requestAnimationFrame(step);
  }
  requestAnimationFrame(step);

  return {
    load: function (id, payload, dotnet) {
      const root = document.getElementById(id);
      if (!root) return;
      const data = typeof payload === 'string' ? JSON.parse(payload) : payload;
      const existing = views.get(id);
      const view = existing || { time: 0, speed: 600, playing: false, segment: -2 };
      view.root = root;
      view.frames = data.frames || [];
      view.duration = data.duration || 86400;
      view.startClock = data.startClock || 0;
      if (dotnet) view.dotnet = dotnet;
      if (view.time > view.duration) view.time = 0;
      views.set(id, view);
      render(view);
    },
    play: function (id) { const v = find(id); if (v) { v.playing = true; v.last = 0; } },
    pause: function (id) { const v = find(id); if (v) { v.playing = false; } },
    toggle: function (id) { const v = find(id); if (v) { v.playing = !v.playing; v.last = 0; } return !!(v && v.playing); },
    speed: function (id, value) { const v = find(id); if (v) v.speed = value; },
    seek: function (id, seconds) { const v = find(id); if (v) { v.time = seconds; render(v); } },
    dispose: function (id) { views.delete(id); }
  };
})();
