namespace SimTelemetry.Remote;

/// <summary>Página del teléfono: todo en un archivo (sin internet ni librerías externas).</summary>
public static class PhonePage
{
    public const string Html = """
<!doctype html>
<html lang="es">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1, viewport-fit=cover">
<meta name="theme-color" content="#0f1217">
<meta name="apple-mobile-web-app-capable" content="yes">
<title>SimTelemetry</title>
<style>
  :root { --bg:#0f1217; --panel:#171b22; --line:#2a303b; --text:#e6ebf2; --dim:#8b95a5; --good:#34d058; --bad:#ff4d4d; --accent:#ffc107; --thr:#34d058; --brk:#ff4d4d; }
  * { box-sizing:border-box; margin:0; padding:0; }
  html, body { height:100%; background:var(--bg); color:var(--text); font-family:system-ui,-apple-system,"Segoe UI",Roboto,sans-serif; overflow:hidden; }
  body { display:grid; grid-template-rows:auto 1fr auto; gap:8px; padding:10px max(10px, env(safe-area-inset-right)) 10px max(10px, env(safe-area-inset-left)); }
  #rpm { height:22px; border-radius:6px; background:var(--panel); overflow:hidden; border:1px solid var(--line); }
  #rpmFill { height:100%; width:0; background:var(--good); transition:width 50ms linear; }
  main { display:grid; grid-template-columns:minmax(120px,1fr) 2.4fr minmax(140px,1.1fr); gap:8px; min-height:0; }
  .card { background:var(--panel); border:1px solid var(--line); border-radius:10px; padding:8px 10px; min-height:0; }
  #gearBox { display:flex; flex-direction:column; align-items:center; justify-content:center; }
  #gear { font-size:min(26vh, 22vw); font-weight:800; line-height:1; }
  #speed { font-size:min(9vh, 8vw); font-weight:700; }
  .unit { color:var(--dim); font-size:12px; }
  #traceBox { position:relative; }
  canvas { width:100%; height:100%; display:block; }
  #side { display:flex; flex-direction:column; gap:6px; justify-content:center; }
  #delta { font-size:min(12vh, 9vw); font-weight:800; text-align:center; }
  #sector { text-align:center; font-size:15px; min-height:20px; }
  .row { display:flex; justify-content:space-between; font-size:13px; color:var(--dim); }
  .row b { color:var(--text); font-variant-numeric:tabular-nums; }
  footer { display:flex; gap:10px; align-items:center; }
  #next { flex:1; font-size:16px; font-weight:600; }
  #status { color:var(--dim); font-size:12px; }
  button { background:var(--panel); color:var(--text); border:1px solid var(--line); border-radius:6px; padding:6px 10px; font-size:13px; }
  .good { color:var(--good); } .bad { color:var(--bad); }
  @media (orientation: portrait) {
    main { grid-template-columns:1fr 1fr; grid-template-rows:auto 1fr; }
    #traceBox { grid-column:1 / span 2; grid-row:2; }
  }
</style>
</head>
<body>
  <div id="rpm"><div id="rpmFill"></div></div>
  <main>
    <div class="card" id="gearBox"><div id="gear">-</div><div id="speed">--</div><div class="unit">km/h</div></div>
    <div class="card" id="traceBox"><canvas id="trace"></canvas></div>
    <div class="card" id="side">
      <div id="delta">--</div>
      <div id="sector"></div>
      <div class="row"><span>Vuelta</span><b id="lap">--</b></div>
      <div class="row"><span>Última</span><b id="last">--</b></div>
      <div class="row"><span>Mejor</span><b id="best">--</b></div>
    </div>
  </main>
  <footer>
    <div id="next"></div>
    <div id="status">Conectando…</div>
    <button id="full">Pantalla completa</button>
  </footer>
<script>
  const $ = id => document.getElementById(id);
  const canvas = $('trace'), ctx = canvas.getContext('2d');
  const history = []; // [t, acelerador, freno]
  const WINDOW = 6;
  let last = null, lastAt = 0;

  function fmt(s) {
    if (!(s > 0)) return '--';
    const m = Math.floor(s / 60), r = s - m * 60;
    return m + ':' + r.toFixed(3).padStart(6, '0');
  }

  function draw() {
    const dpr = window.devicePixelRatio || 1;
    const w = canvas.clientWidth, h = canvas.clientHeight;
    if (canvas.width !== Math.round(w * dpr)) { canvas.width = Math.round(w * dpr); canvas.height = Math.round(h * dpr); }
    ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
    ctx.clearRect(0, 0, w, h);
    const bars = 46, pw = w - bars - 8;
    ctx.strokeStyle = 'rgba(255,255,255,0.12)'; ctx.lineWidth = 1;
    for (const f of [0.25, 0.5, 0.75]) { ctx.beginPath(); ctx.moveTo(0, h - f * h); ctx.lineTo(pw, h - f * h); ctx.stroke(); }
    if (history.length > 1) {
      const now = history[history.length - 1][0];
      for (const [i, color] of [[1, '#34d058'], [2, '#ff4d4d']]) {
        ctx.strokeStyle = color; ctx.lineWidth = 2.5; ctx.beginPath();
        history.forEach((p, k) => {
          const x = pw - (now - p[0]) / WINDOW * pw, y = h - 4 - p[i] * (h - 8);
          k ? ctx.lineTo(x, y) : ctx.moveTo(x, y);
        });
        ctx.stroke();
      }
    }
    if (last) {
      const bw = 18;
      for (const [k, v, color] of [[0, last.brk, '#ff4d4d'], [1, last.thr, '#34d058']]) {
        const x = pw + 8 + k * (bw + 6);
        ctx.fillStyle = 'rgba(255,255,255,0.15)'; ctx.fillRect(x, 0, bw, h);
        ctx.fillStyle = color; ctx.fillRect(x, h - v * h, bw, v * h);
      }
    }
  }

  function update(d) {
    last = d; lastAt = Date.now();
    history.push([d.t, d.thr, d.brk]);
    while (history.length && d.t - history[0][0] > WINDOW + 0.5) history.shift();
    if (history.length > 1 && history[history.length - 2][0] > d.t) history.length = 0; // la app se reinició

    $('gear').textContent = !d.vehicle ? '-' : d.gear < 0 ? 'R' : d.gear === 0 ? 'N' : d.gear;
    $('speed').textContent = d.vehicle ? Math.round(d.kmh) : '--';
    const frac = d.maxRpm > 0 ? Math.min(1, d.rpm / d.maxRpm) : 0;
    $('rpmFill').style.width = (frac * 100) + '%';
    $('rpmFill').style.background = frac > 0.96 ? '#4da3ff' : frac > 0.9 ? '#ff4d4d' : frac > 0.8 ? '#ffc107' : '#34d058';

    const delta = $('delta');
    if (typeof d.delta === 'number') { delta.textContent = (d.delta >= 0 ? '+' : '') + d.delta.toFixed(2); delta.className = d.delta <= 0 ? 'good' : 'bad'; }
    else { delta.textContent = '--'; delta.className = ''; }
    const sec = $('sector');
    if (d.sector) { sec.textContent = 'Curva ' + d.sector.c + ': ' + (d.sector.d >= 0 ? '+' : '') + d.sector.d.toFixed(2); sec.className = d.sector.d <= 0 ? 'good' : 'bad'; }
    else sec.textContent = '';
    $('lap').textContent = fmt(d.lap); $('last').textContent = fmt(d.last); $('best').textContent = fmt(d.best);
    $('next').textContent = d.next ? 'Curva ' + d.next.c + ' · ' + d.next.text + ' · en ' + Math.max(0, Math.round(d.next.m)) + ' m' : '';
    $('status').textContent = d.game ? d.game + (d.track ? ' · ' + d.track : '') : (d.wheel ? 'Volante (sin juego)' : 'Esperando un juego…');
    requestAnimationFrame(draw);
  }

  function connect() {
    const es = new EventSource(location.pathname.replace(/\/$/, '') + '/stream');
    es.onmessage = e => { try { update(JSON.parse(e.data)); } catch (_) {} };
    es.onerror = () => { $('status').textContent = 'Sin conexión con el PC… reintentando'; };
  }
  setInterval(() => { if (Date.now() - lastAt > 2000) $('status').textContent = 'Sin datos del PC (¿está abierta SimTelemetry?)'; }, 1000);
  $('full').onclick = () => (document.documentElement.requestFullscreen || document.documentElement.webkitRequestFullscreen || (() => {})).call(document.documentElement);
  window.addEventListener('resize', draw);
  connect();
</script>
</body>
</html>
""";
}
