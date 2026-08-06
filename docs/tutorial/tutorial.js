/* ============================================================================
   LLM NPC 서버 — 활용 실습서 공용 스크립트
   장을 추가하면 CHAPTERS 만 고친다. 사이드바 · 페이저 · 진행 막대가 따라온다.
   ========================================================================== */
(function () {
  'use strict';

  /* status: 'done' = 본문이 있다 · 'todo' = 목차만 잡혀 있다 */
  var CHAPTERS = [
    { part: '표지', file: 'index.html', no: '표지', title: '목차와 읽는 법', status: 'done',
      desc: '이 책이 무엇이고 어떤 순서로 무엇을 만드는가' },

    { part: '0부 · 준비', file: 'ch00.html', no: '0장', title: '시작하기 전에', status: 'done',
      desc: '환경 확인 · 기준선 만들기 · 실습 골격 lab.ps1 과 check.ps1' },

    { part: '1부 · 일단 켠다', file: 'ch01.html', no: '1장', title: '5분 만에 NPC를 살린다', status: 'done',
      desc: '명령 한 줄로 NPC 20마리가 하루를 산다. 옵션 6개와 대조 실험 8회' },
    { part: '1부 · 일단 켠다', file: 'ch02.html', no: '2장', title: 'NPC 한 마리를 따라간다', status: 'done',
      desc: '관측 4종 · NPC 추적기 만들기 · "왜 저기로 가는가"에 답하기' },
    { part: '1부 · 일단 켠다', file: 'ch03.html', no: '3장', title: '눈으로 본다 — 3프로세스', status: 'done',
      desc: '소켓으로 붙인 데모 · 누가 무엇을 아는가 · 핸드셰이크를 어긋내 보기' },

    { part: '2부 · 세계를 바꾼다', file: 'ch04.html', no: '4장', title: '마스터데이터 지도와 검증기', status: 'done',
      desc: '11개 파일의 참조 관계 · V1~V11 · 아홉 가지로 깨뜨려 보기' },
    { part: '2부 · 세계를 바꾼다', file: 'ch05.html', no: '5장', title: '마을에 없던 것을 만든다', status: 'done',
      desc: '아이템 honey · 양봉장 POI · 거리표 재생성 · content_hash' },
    { part: '2부 · 세계를 바꾼다', file: 'ch06.html', no: '6장', title: '하루 일과를 손으로 쓴다', status: 'done',
      desc: '플랜 DSL · 심볼 · 스텝 3~10 상한 · 검증기 4단' },
    { part: '2부 · 세계를 바꾼다', file: 'ch07.html', no: '7장', title: '새 직업을 만든다', status: 'done',
      desc: 'beekeeper 추가 · ArchetypeCount 40→41 · 테스트 18개가 깨진다' },
    { part: '2부 · 세계를 바꾼다', file: 'ch08.html', no: '8장', title: '반사신경 — 인터럽트', status: 'done',
      desc: '규칙 하나로 발동 +375건 · 엣지 트리거 · cooldown 이 없는 이유' },
    { part: '2부 · 세계를 바꾼다', file: 'ch09.html', no: '9장', title: '새 행동을 추가한다', status: 'done',
      desc: '액션 Tend 추가 · 프리픽스 SHA 변경 · 플랜 스토어 전량 무효' },

    { part: '3부 · 세계를 흔든다', file: 'index.html#ch10', no: '10장', title: '사건 대본을 쓴다', status: 'todo' },
    { part: '3부 · 세계를 흔든다', file: 'index.html#ch11', no: '11장', title: '일부러 부러뜨린다', status: 'todo' },

    { part: '4부 · 내 코드에 붙인다', file: 'index.html#ch12', no: '12장', title: '가장 작은 링크', status: 'todo' },
    { part: '4부 · 내 코드에 붙인다', file: 'index.html#ch13', no: '13장', title: '소켓 게임서버를 만든다', status: 'todo' },
    { part: '4부 · 내 코드에 붙인다', file: 'index.html#ch14', no: '14장', title: '다른 언어에서 붙는다', status: 'todo' },
    { part: '4부 · 내 코드에 붙인다', file: 'index.html#ch15', no: '15장', title: '기록하고 되감는다', status: 'todo' },

    { part: '5부 · LLM을 켠다', file: 'index.html#ch16', no: '16장', title: '티어를 켠다', status: 'todo' },
    { part: '5부 · LLM을 켠다', file: 'index.html#ch17', no: '17장', title: '플랜을 미리 굽는다', status: 'todo' },
    { part: '5부 · LLM을 켠다', file: 'index.html#ch18', no: '18장', title: '실패한 플랜을 읽는다', status: 'todo' },

    { part: '6부 · 제품으로', file: 'index.html#ch19', no: '19장', title: '부하로 확인한다', status: 'todo' },
    { part: '6부 · 제품으로', file: 'index.html#ch20', no: '20장', title: '테스트로 지킨다', status: 'todo' }
  ];

  var here = (location.pathname.split('/').pop() || 'index.html').toLowerCase();
  if (here === '') here = 'index.html';

  /* 본문이 있는 장만 페이저로 잇는다. 아직 안 쓴 장으로 넘기면 표지로 튕긴다. */
  var LIVE = CHAPTERS.filter(function (c) { return c.status === 'done'; });
  var idx = LIVE.findIndex(function (c) { return c.file === here; });
  if (idx < 0) idx = 0;

  function esc(s) {
    return String(s).replace(/[&<>"]/g, function (c) {
      return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c];
    });
  }

  function buildNav() {
    var nav = document.getElementById('toc');
    if (!nav) return;

    var html = '';
    html += '<div class="brand"><span class="dot"></span><b>활용 실습서</b></div>';
    html += '<div class="sub">LLM NPC 서버 · 손으로 만들며 배우기</div>';

    var part = null;
    CHAPTERS.forEach(function (c) {
      if (c.part !== part) {
        part = c.part;
        html += '<div class="grp">' + esc(part) + '</div>';
      }
      var cur = (c.file === here && c.status === 'done') ? ' class="cur"' : '';
      var dim = c.status === 'todo' ? ' style="opacity:.5"' : '';
      html += '<a href="' + c.file + '"' + cur + dim + '>' +
        '<span class="n">' + esc(c.no) + '</span>' + esc(c.title) + '</a>';
    });

    html += '<div class="inpage" id="inpage"></div>';
    html += '<div class="tools">' +
      '<button type="button" id="themeBtn">테마</button>' +
      '<a href="../book/index.html">코드 안내서</a>' +
      '<a href="../index.html">한장 안내서</a>' +
      '</div>';

    nav.innerHTML = html;
  }

  function buildInPage() {
    var box = document.getElementById('inpage');
    if (!box) return;
    var hs = document.querySelectorAll('main h2[id]');
    if (!hs.length) { box.remove(); return; }

    var html = '<div class="grp">이 장 안에서</div>';
    hs.forEach(function (h) {
      html += '<a href="#' + h.id + '" data-target="' + h.id + '">' + esc(h.textContent) + '</a>';
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

  function buildPager() {
    var main = document.querySelector('main');
    if (!main || document.querySelector('.pager')) return;

    var prev = idx > 0 ? LIVE[idx - 1] : null;
    var next = idx < LIVE.length - 1 ? LIVE[idx + 1] : null;
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

  function wireTheme() {
    var root = document.documentElement;
    var saved = null;
    try { saved = localStorage.getItem('npctut.theme'); } catch (e) { /* file:// 제한 */ }
    if (saved) root.setAttribute('data-theme', saved);

    var tb = document.getElementById('themeBtn');
    if (!tb) return;
    function label(v) { return v === 'auto' ? '테마' : v === 'light' ? '테마 · 밝게' : '테마 · 어둡게'; }
    tb.textContent = label(root.getAttribute('data-theme') || 'auto');
    tb.addEventListener('click', function () {
      var cur = root.getAttribute('data-theme') || 'auto';
      var next = cur === 'auto' ? 'light' : cur === 'light' ? 'dark' : 'auto';
      root.setAttribute('data-theme', next);
      try { localStorage.setItem('npctut.theme', next); } catch (e) { /* 무시 */ }
      tb.textContent = label(next);
    });
  }

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

  window.NpcTutorial = { chapters: CHAPTERS };

  document.addEventListener('DOMContentLoaded', function () {
    buildNav();
    buildInPage();
    buildPager();
    wireTheme();
    wireProgress();
  });
})();
