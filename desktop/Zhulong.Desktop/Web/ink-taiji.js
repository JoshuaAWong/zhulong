"use strict";
// 弧长表示真实占比；中心太极保持传统等分，只承担视觉识别。
function taijiMarkup(value, style) {
  const core = '<circle cx="60" cy="60" r="28" class="taiji-paper"/>' +
    '<path d="M60 32a28 28 0 0 1 0 56a14 14 0 0 1 0-28a14 14 0 0 0 0-28Z" class="taiji-ink"/>' +
    '<circle cx="60" cy="46" r="3.6" class="taiji-ink"/><circle cx="60" cy="74" r="3.6" class="taiji-paper"/>';
  const width = style === "sector" ? 19 : style === "fish" ? 12 : 5;
  const ring = `<circle cx="60" cy="60" r="47" class="ratio-track" stroke-width="${width}"/>` +
    `<circle cx="60" cy="60" r="47" class="ratio-value" stroke-width="${width}" pathLength="100" stroke-dasharray="${value} 100" transform="rotate(-90 60 60)"/>`;
  let accents = "";
  if (style === "ring") accents = `<circle cx="60" cy="60" r="47" class="ratio-halo" pathLength="100" stroke-dasharray="${value} 100" transform="rotate(-90 60 60)"/>`;
  if (style === "fish") {
    // 鱼眼只放在足够长的分弧内部；0% 与 100% 不画不存在的一侧。
    for (const [size, end, className] of [[value, value, "taiji-paper"], [100 - value, 100, "taiji-ink"]]) {
      if (size <= 5) continue;
      const angle = (end / 100 * 360 - 97) * Math.PI / 180;
      accents += `<circle cx="${(60 + 47 * Math.cos(angle)).toFixed(3)}" cy="${(60 + 47 * Math.sin(angle)).toFixed(3)}" r="2.4" class="${className}"/>`;
    }
  }
  if (style === "sector") accents = '<path d="M60 2v5m58 53h-5M60 118v-5M2 60h5" class="ratio-ticks"/>';
  return `<svg class="taiji-svg taiji-${style}" viewBox="0 0 120 120" aria-hidden="true">${ring}${accents}<g class="taiji-core">${core}</g></svg>`;
}
function renderTaiji() {
  const style = root.dataset.taijiStyle || { ridge: "ring", breath: "fish", scroll: "sector" }[root.dataset.atmosphere];
  for (const [name, value] of [["cpu", 14], ["gpu", 6]]) {
    const resource = $(`.resource[data-detail="${name}"]`);
    resource.querySelector("svg").outerHTML = taijiMarkup(value, style);
  }
}
document.addEventListener("click", event => {
  if (event.target.closest("[data-atmosphere-choice]")) renderTaiji();
});
renderTaiji();

// 主仪表独占鱼身和涟漪；读数、换肤与悬停聚卦仍由原控件负责。
function createFluidTaiji(svg) {
  const radius = 52, center = 60, top = center - radius, bottom = center + radius;
  svg.classList.add("fluid-taiji");
  svg.innerHTML = `<defs>
    <radialGradient id="fluid-ink" cx=".23" cy=".20" r=".86"><stop stop-color="var(--fluid-wash)"/><stop offset=".55" stop-color="var(--taiji-ink)"/></radialGradient>
    <clipPath id="fluid-clip"><circle cx="60" cy="60" r="${radius}"/></clipPath>
    </defs><g class="taiji-core"><g class="taiji-spirit">
    <circle class="taiji-paper" cx="60" cy="60" r="${radius}"/><path class="fluid-fish"/>
    <circle class="taiji-ink" cx="60"/><circle class="taiji-paper" cx="60"/>
    </g><g clip-path="url(#fluid-clip)" aria-hidden="true"><circle class="fluid-wave" r="52"/><circle class="fluid-wave" r="52"/></g></g>`;
  const rotor = svg.querySelector(".taiji-spirit");
  const [, fish, inkEye, paperEye] = rotor.children;
  const waves = Array.from(svg.querySelectorAll(".fluid-wave"));
  let target = null, shown = 50, heat = 0, hovered = false, visible = false;
  let rotation = null, breathing = null, frame = 0, lastTime = 0;

  function draw() {
    const lower = radius * shown / 100, upper = radius - lower;
    const eye = Math.min(lower, upper) * .23;
    inkEye.setAttribute("cy", top + upper);
    paperEye.setAttribute("cy", bottom - lower);
    for (const dot of [inkEye, paperEye]) dot.setAttribute("r", eye);
    // 两段半椭圆的横向增量分别乘以对方的纵半径，增减的墨面积始终相抵。
    function contour(bend) {
      if (shown === 0) return `M60 ${top}Z`;
      const outer = `M60 ${top}a${radius} ${radius} 0 0 1 0 ${2 * radius}`;
      if (shown === 100) return outer + `a${radius} ${radius} 0 0 1 0 ${-2 * radius}Z`;
      return outer + `a${lower + bend * upper / radius} ${lower} 0 0 1 0 ${-2 * lower}` +
        `a${upper + bend * lower / radius} ${upper} 0 0 0 0 ${-2 * upper}Z`;
    }
    fish.setAttribute("d", contour(0));
    if (visible && target !== null && eye > 0) {
      const amplitude = (2 + heat * 3) * Math.min(lower, upper) / (radius / 2);
      const frames = [-amplitude, amplitude].map(bend => ({ d: 'path("' + contour(bend) + '")' }));
      if (breathing) breathing.effect.setKeyframes(frames);
      else breathing = fish.animate(frames, { duration:1800, direction:"alternate", iterations:Infinity, easing:"ease-in-out" });
    } else {
      breathing?.cancel();
      breathing = null;
    }
    if (visible && target !== null) {
      if (!rotation) rotation = rotor.animate([{ transform:"rotate(0deg)" }, { transform:"rotate(360deg)" }],
        { duration:60000, iterations:Infinity, easing:"linear" });
      else if (rotation.playState === "paused") rotation.play();
      rotation.updatePlaybackRate(taijiRate(shown) * (1 + heat * 2));
    } else rotation?.pause();
  }

  function approach(from, to, elapsed, seconds) {
    const next = to + (from - to) * Math.exp(-elapsed / seconds);
    return Math.abs(next - to) < .002 ? to : next;
  }
  function step(time) {
    frame = 0;
    const elapsed = Math.min(Math.max(0, time - lastTime), 80) / 1000;
    lastTime = time;
    shown = approach(shown, target, elapsed, .105);
    heat = approach(heat, Number(hovered), elapsed, hovered ? .16 : .26);
    draw();
    if (shown !== target || heat !== Number(hovered)) frame = requestAnimationFrame(step);
  }
  function sync() {
    if (!visible || target === null) {
      cancelAnimationFrame(frame);
      frame = 0;
      shown = target ?? 50;
      heat = 0;
      for (const wave of waves) wave.getAnimations().forEach(animation => animation.cancel());
    } else if (!frame && (shown !== target || heat !== Number(hovered))) {
      lastTime = performance.now();
      frame = requestAnimationFrame(step);
    }
    draw();
  }
  function ripple(event) {
    if (!visible) return;
    const point = event ? new DOMPoint(event.clientX, event.clientY).matrixTransform(svg.getScreenCTM().inverse()) :
      new DOMPoint(center, center);
    const dx = point.x - center, dy = point.y - center;
    const scale = Math.min(1, (radius - 4) / (Math.hypot(dx, dy) || 1));
    waves.forEach((wave, index) => {
      wave.getAnimations().forEach(animation => animation.cancel());
      wave.setAttribute("cx", center + dx * scale);
      wave.setAttribute("cy", center + dy * scale);
      wave.animate([{ opacity:.8, transform:"scale(.04)" }, { opacity:0, transform:"scale(1)" }],
        { duration:1100, delay:index * 210, easing:"ease-out" });
    });
  }
  svg.dataset.missing = "true";
  draw();
  return {
    setRatio(value) {
      if (value === target) return;
      if (target === null && value !== null) shown = value;
      target = value;
      svg.dataset.missing = String(value === null);
      sync();
    },
    setMotion(allowed, over) {
      if (visible === allowed && hovered === over) return;
      visible = allowed;
      hovered = over;
      sync();
    },
    ripple
  };
}
