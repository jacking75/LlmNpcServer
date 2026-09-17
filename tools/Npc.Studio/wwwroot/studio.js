// H10·H18·H19·H24 — Studio 정적 스크립트. 빌드가 없다.
// 여기 있는 것은 <b>브라우저만 아는 것</b> 뿐이다 — 요소 위치, 키보드, SVG 좌표계.
// 판정·계산은 전부 서버에 있다.
window.studio = (function () {
  // H10 — "고치러 가기" 가 도착한 뒤 그 칸으로 스크롤하고 2초 강조한다.
  function focusField(id) {
    if (!id) return false;
    const el = document.querySelector('[data-field="' + cssEscape(id) + '"]');
    if (!el) return false;

    el.scrollIntoView({ behavior: 'smooth', block: 'center' });
    el.classList.remove('field-flash');
    // 클래스를 다시 붙이려면 리플로가 한 번 있어야 애니메이션이 재시작한다.
    void el.offsetWidth;
    el.classList.add('field-flash');

    // ⓘ 버튼보다 <b>입력 칸</b>이 먼저다 — 버튼을 잡으면 :focus-within 이 설명을 펼쳐 칸을 덮는다.
    const focusable = el.querySelector('input,select,textarea') || el.querySelector('button');
    if (focusable) { try { focusable.focus({ preventScroll: true }); } catch (e) { /* 무시 */ } }

    window.setTimeout(function () { el.classList.remove('field-flash'); }, 2200);
    return true;
  }

  function cssEscape(value) {
    return String(value).replace(/["\\]/g, '\\$&');
  }

  // H02·H24 — 모달이 열리면 첫 버튼에 포커스. 없으면 조용히 지나간다.
  function focusElement(id) {
    const el = document.getElementById(id);
    if (!el) return false;
    try { el.focus({ preventScroll: true }); } catch (e) { return false; }
    return true;
  }

  // H19 — Ctrl/Cmd+K 가 전역 검색으로 간다. 입력 중이면 가로채지 않는다(Escape 로 빠진다).
  let searchBound = false;

  function bindSearch() {
    if (searchBound) return;
    searchBound = true;

    document.addEventListener('keydown', function (e) {
      if ((e.ctrlKey || e.metaKey) && (e.key === 'k' || e.key === 'K')) {
        const box = document.getElementById('global-search');
        if (box) { e.preventDefault(); box.focus(); box.select(); }
      }
    });
  }

  // H18 — SVG 안의 클릭 좌표를 viewBox 좌표로 되돌린다.
  // getScreenCTM 없이 계산하면 preserveAspectRatio 여백만큼 어긋난다.
  function svgPoint(id, clientX, clientY) {
    const svg = document.getElementById(id);
    if (!svg || !svg.getScreenCTM) return null;
    const ctm = svg.getScreenCTM();
    if (!ctm) return null;

    const point = svg.createSVGPoint();
    point.x = clientX;
    point.y = clientY;

    const local = point.matrixTransform(ctm.inverse());
    return { x: Math.round(local.x * 100) / 100, z: Math.round(local.y * 100) / 100 };
  }

  return {
    focusField: focusField,
    focusElement: focusElement,
    bindSearch: bindSearch,
    svgPoint: svgPoint,
  };
})();
