/* ============================================================================
   LLM NPC 서버 — 코드 안내서 (책) 공용 스크립트
   - 목차·페이저를 여기 한 곳에서 만든다. 장을 추가하면 CHAPTERS 만 고친다.
   - 테마·애니메이션 스위치, 본문 소제목 미니 목차, 진행 막대, 키보드 이동.
   ========================================================================== */
(function () {
  'use strict';

  var CHAPTERS = [
    { part: '들어가며', file: 'index.html', no: '표지', title: '이 책을 읽는 법',
      desc: '무엇을 다루고 무엇을 안 다루는가 · 독자별 경로 · 프로젝트 현황' },

    { part: '1부 — 왜 이 구조인가', file: 'ch01.html', no: '1장', title: '문제와 해답',
      desc: 'NPC 5,000마리에 LLM을 그냥 붙이면 왜 안 되는가. L0~L3 4계층이 나온 산술.' },

    { part: '2부 — 공통 계약', file: 'ch02.html', no: '2장', title: '마스터데이터 — 단일 원천',
      desc: '64비트 월드 플래그 · 액션 37종 · 아키타입 40종 · 버킷 2,880 · 검증 V1~V13.' },
    { part: '2부 — 공통 계약', file: 'ch03.html', no: '3장', title: '게임서버 링크 계약',
      desc: 'N1~N8 · 명령 12종 / 이벤트 17종 · 명령은 유실된다는 전제 · 링크 구현체 5종.' },
    { part: '2부 — 공통 계약', file: 'ch04.html', no: '4장', title: '플랜 DSL과 4단 검증기',
      desc: 'LLM이 내는 유일한 산출물 · JSON에서 CompiledPlan까지 · 무엇을 어떻게 반려하나.' },

    { part: '3부 — 결정론 런타임 L0', file: 'ch05.html', no: '5장', title: '틱 루프와 SoA 저장소',
      desc: '10Hz · 예산 20ms · await 두 개 · 할당 0 · class Npc를 5,000개 만들지 않는 이유.' },
    { part: '3부 — 결정론 런타임 L0', file: 'ch06.html', no: '6장', title: '인지 LOD와 재계획 큐',
      desc: '5,000마리 중 누구를 볼 것인가. 밴드·슬라이스·상한 150 · 점수 힙 · 가중치 A/B.' },
    { part: '3부 — 결정론 런타임 L0', file: 'ch07.html', no: '7장', title: '플랜 실행·인터럽트·전환',
      desc: '스텝 상태기계 · 타임아웃 합성 · 상관 ID · 인터럽트 규칙 · 버킷 전환 지터.' },

    { part: '4부 — 플랜 생성 L3', file: 'ch08.html', no: '8장', title: '프롬프트와 LLM 컴파일러',
      desc: '프리픽스 13,488 토큰과 서픽스 300 토큰 · 캐시 · 재시도 1회 · 구조 3단.' },
    { part: '4부 — 플랜 생성 L3', file: 'ch09.html', no: '9장', title: '티어·예산·캐시·프리베이크',
      desc: 'T0/T1/T2 라우팅 · 상한 4개 · 플랜 스토어 · 2,880 중 264만 만들면 되는 이유.' },

    { part: '5부 — 경계 밖', file: 'ch10.html', no: '10장', title: '전송과 테스트 베드',
      desc: '와이어 프레임 · 핸드셰이크 4값 · 소켓 게임서버 대역 · WinForms 뷰어 (P6).' },

    { part: '6부 — 돌려보고 고치기', file: 'ch11.html', no: '11장', title: '설정을 바꾸면 무엇이 달라지나',
      desc: '실행 옵션 26개 · appsettings.Llm.json · 마스터데이터 상수 → 동작 변화 매트릭스.' },
    { part: '6부 — 돌려보고 고치기', file: 'ch12.html', no: '12장', title: '관측 · 테스트 · 게이트 · 실측',
      desc: '/metrics 읽는 법 · 테스트 카테고리 8종 · P1~P6 게이트 결과 · 빗나간 추정들.' },

    { part: '부록', file: 'ch13.html', no: '부록', title: '코드 맵 · 확장 레시피 · 용어집',
      desc: '파일별 한 줄 지도 · 액션/아키타입 추가 절차 · 자주 하는 실수 · 용어 대조표.' }
  ];

  var here = (location.pathname.split('/').pop() || 'index.html').toLowerCase();
  if (here === '') here = 'index.html';
  var idx = CHAPTERS.findIndex(function (c) { return c.file === here; });
  if (idx < 0) idx = 0;

  /* ---------- 사이드바 ---------- */
  function buildNav() {
    var nav = document.getElementById('toc');
    if (!nav) return;

    var html = '';
    html += '<div class="brand"><span class="dot"></span><b>LLM NPC 서버</b></div>';
    html += '<div class="sub">코드 이해와 활용 안내서</div>';

    var part = null;
    CHAPTERS.forEach(function (c, i) {
      if (c.part !== part) {
        part = c.part;
        html += '<div class="part">' + esc(part) + '</div>';
      }
      html += '<a href="' + c.file + '"' + (i === idx ? ' class="cur"' : '') + '>' +
        '<span class="n">' + esc(c.no) + '</span>' + esc(c.title) + '</a>';
    });

    html += '<div class="inpage" id="inpage"></div>';
    html += '<div class="tools">' +
      '<button type="button" id="themeBtn">테마</button>' +
      '<button type="button" id="motionBtn">애니메이션</button>' +
      '<a href="../index.html">한장 안내서</a>' +
      '<a href="../../README.md">README</a>' +
      '</div>';

    nav.innerHTML = html;
  }

  /* ---------- 본문 소제목 미니 목차 ---------- */
  function buildInPage() {
    var box = document.getElementById('inpage');
    if (!box) return;
    var hs = document.querySelectorAll('main h2[id]');
    if (!hs.length) { box.remove(); return; }

    var html = '<div class="part">이 장 안에서</div>';
    hs.forEach(function (h) {
      html += '<a href="#' + h.id + '" data-target="' + h.id + '">' +
        esc(h.textContent.replace(/^\s*\d+\.\s*/, '')) + '</a>';
    });
    box.innerHTML = html;

    var links = box.querySelectorAll('a[data-target]');
    var io = new IntersectionObserver(function (entries) {
      entries.forEach(function (e) {
        if (!e.isIntersecting) return;
        links.forEach(function (a) {
          a.classList.toggle('here', a.dataset.target === e.target.id);
        });
      });
    }, { rootMargin: '-10% 0px -80% 0px' });
    hs.forEach(function (h) { io.observe(h); });
  }

  /* ---------- 페이저 ---------- */
  function buildPager() {
    var main = document.querySelector('main');
    if (!main || document.querySelector('.pager')) return;

    var prev = idx > 0 ? CHAPTERS[idx - 1] : null;
    var next = idx < CHAPTERS.length - 1 ? CHAPTERS[idx + 1] : null;
    if (!prev && !next) return;

    var div = document.createElement('div');
    div.className = 'pager';
    var html = '';
    if (prev) {
      html += '<a href="' + prev.file + '" class="prev"><div class="dir">← 이전 · ' +
        esc(prev.no) + '</div><div class="ttl">' + esc(prev.title) + '</div></a>';
    } else {
      html += '<span style="flex:1 1 250px"></span>';
    }
    if (next) {
      html += '<a href="' + next.file + '" class="next"><div class="dir">다음 · ' +
        esc(next.no) + ' →</div><div class="ttl">' + esc(next.title) + '</div></a>';
    }
    div.innerHTML = html;
    main.appendChild(div);

    document.addEventListener('keydown', function (e) {
      if (e.target && /INPUT|TEXTAREA|SELECT/.test(e.target.tagName)) return;
      if (e.altKey || e.ctrlKey || e.metaKey) return;
      if (e.key === 'ArrowLeft' && prev) location.href = prev.file;
      if (e.key === 'ArrowRight' && next) location.href = next.file;
    });
  }

  /* ---------- 표지 목차 카드 ---------- */
  function buildTocGrid() {
    var box = document.getElementById('tocgrid');
    if (!box) return;
    var html = '';
    CHAPTERS.slice(1).forEach(function (c) {
      html += '<a class="tocard" href="' + c.file + '">' +
        '<div class="no">' + esc(c.no) + '</div>' +
        '<div class="t">' + esc(c.title) + '</div>' +
        '<div class="d">' + esc(c.desc) + '</div></a>';
    });
    box.innerHTML = html;
  }

  /* ---------- 테마 · 애니메이션 ---------- */
  function wireTools() {
    var root = document.documentElement;
    var saved = null;
    try { saved = localStorage.getItem('npcbook.theme'); } catch (e) { /* file:// 제한 */ }
    if (saved) root.setAttribute('data-theme', saved);

    var tb = document.getElementById('themeBtn');
    if (tb) {
      tb.addEventListener('click', function () {
        var cur = root.getAttribute('data-theme') || 'auto';
        var next = cur === 'auto' ? 'light' : cur === 'light' ? 'dark' : 'auto';
        root.setAttribute('data-theme', next);
        try { localStorage.setItem('npcbook.theme', next); } catch (e) { /* 무시 */ }
        tb.textContent = next === 'auto' ? '테마' : next === 'light' ? '테마 · 밝게' : '테마 · 어둡게';
      });
    }

    var mb = document.getElementById('motionBtn');
    if (mb) {
      mb.addEventListener('click', function () {
        var off = document.body.classList.toggle('no-motion');
        mb.textContent = off ? '애니메이션 · 정지' : '애니메이션';
        window.NpcBook.motion = !off;
        window.dispatchEvent(new CustomEvent('npcbook:motion', { detail: { on: !off } }));
      });
    }
  }

  /* ---------- 진행 막대 ---------- */
  function wireProgress() {
    var bar = document.createElement('div');
    bar.id = 'progress';
    document.body.appendChild(bar);
    function update() {
      var h = document.documentElement;
      var max = h.scrollHeight - h.clientHeight;
      bar.style.width = (max <= 0 ? 0 : (h.scrollTop / max) * 100) + '%';
    }
    window.addEventListener('scroll', update, { passive: true });
    window.addEventListener('resize', update);
    update();
  }

  function esc(s) {
    return String(s).replace(/[&<>"]/g, function (c) {
      return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c];
    });
  }

  /* ---------- 애니메이션 헬퍼 ---------- *
     각 장의 인터랙티브 그림이 공통으로 쓴다.
     - loop(fn, ms) : 정지 스위치를 존중하는 setInterval
     - onVisible(el, fn) : 화면에 들어올 때 한 번 시작
  */
  var timers = [];
  window.NpcBook = {
    motion: true,
    chapters: CHAPTERS,
    loop: function (fn, ms) {
      var id = setInterval(function () { if (window.NpcBook.motion) fn(); }, ms);
      timers.push(id);
      return id;
    },
    stop: function (id) { clearInterval(id); },
    onVisible: function (el, fn) {
      if (!el) return;
      var done = false;
      var io = new IntersectionObserver(function (es) {
        es.forEach(function (e) {
          if (e.isIntersecting && !done) { done = true; fn(); io.disconnect(); }
        });
      }, { rootMargin: '0px 0px -15% 0px' });
      io.observe(el);
    },
    /* SVG 만들 때 쓰는 짧은 도우미 */
    el: function (name, attrs, parent) {
      var e = document.createElementNS('http://www.w3.org/2000/svg', name);
      for (var k in attrs) { if (attrs[k] != null) e.setAttribute(k, attrs[k]); }
      if (parent) parent.appendChild(e);
      return e;
    }
  };

  document.addEventListener('DOMContentLoaded', function () {
    buildNav();
    buildInPage();
    buildTocGrid();
    buildPager();
    wireTools();
    wireProgress();
  });
})();
