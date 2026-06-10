namespace SphereNet.Server.Admin;

/// <summary>
/// Embedded HTML for the /dialogedit live dialog designer. Left pane: the
/// raw [DIALOG] layout script (loadable from the loaded packs); right pane:
/// a live preview rendered with the real gump art served from /gump/{id}.png.
/// The JS coordinate walk mirrors ClientDialogHandler.ResolveDialogCoord
/// (N absolute, +N/-N row-relative, *N row step, DORIGIN offsets).
/// </summary>
internal static class DialogEditorPage
{
    public const string Html = """
<!DOCTYPE html>
<html lang="tr">
<head>
<meta charset="utf-8">
<title>SphereNet Dialog Tasarımcısı</title>
<style>
  * { box-sizing: border-box; }
  body { margin:0; font:13px/1.4 'Segoe UI',sans-serif; background:#1b1d23; color:#ddd;
         display:flex; flex-direction:column; height:100vh; }
  header { padding:8px 14px; background:#242730; display:flex; gap:10px; align-items:center;
           border-bottom:1px solid #333; flex-wrap:wrap; }
  header h1 { font-size:15px; margin:0 14px 0 0; color:#7ec8ff; }
  select,button,input { background:#2e323d; color:#ddd; border:1px solid #444;
           border-radius:4px; padding:4px 8px; font-size:13px; }
  button:hover { background:#3a4050; cursor:pointer; }
  main { flex:1; display:flex; min-height:0; }
  #left { width:520px; min-width:300px; display:flex; flex-direction:column; border-right:1px solid #333; }
  #src { flex:1; resize:none; border:0; background:#14161b; color:#cde;
         font:12px/1.5 Consolas,monospace; padding:10px; outline:none; white-space:pre; }
  #right { flex:1; overflow:auto; background:
        repeating-conic-gradient(#20232a 0% 25%, #262a33 0% 50%) 0 0/24px 24px; position:relative; }
  #stage { position:relative; margin:24px; min-width:600px; min-height:400px; }
  .ctl { position:absolute; }
  .ph { background:rgba(180,60,60,.35); border:1px dashed #d66; color:#fbb;
        font-size:10px; overflow:hidden; }
  .txt { white-space:nowrap; text-shadow:1px 1px 1px #000; font:13px/1.2 'Palatino Linotype',serif; }
  .crop { overflow:hidden; }
  .html { overflow:auto; font:12px/1.3 'Palatino Linotype',serif; color:#222; }
  .entry { background:#fff; color:#000; border:1px inset #999; font:12px Consolas,monospace;
           overflow:hidden; white-space:nowrap; }
  .expr { color:#8fd; background:rgba(40,90,80,.5); border-radius:2px; padding:0 2px; }
  #info { padding:4px 10px; background:#242730; border-top:1px solid #333; color:#9ab;
          font-size:12px; min-height:22px; }
  .tiled { background-repeat:repeat; image-rendering:pixelated; }
  img.ctl { image-rendering:pixelated; }
</style>
</head>
<body>
<header>
  <h1>Dialog Tasarımcısı</h1>
  <select id="dlgList"><option value="">— dialog seç —</option></select>
  <button onclick="loadDialog()">Yükle</button>
  <label>Sayfa: <select id="pageSel"><option value="all">tümü</option></select></label>
  <label><input type="checkbox" id="showBounds"> kutu çerçeveleri</label>
  <button onclick="render()">Yenile</button>
  <span id="stat"></span>
</header>
<main>
  <div id="left"><textarea id="src" spellcheck="false" placeholder="[DIALOG d_ornek]
0,0
RESIZEPIC 0 0 2600 400 300
DTEXT 40 30 90 Merhaba dünya
BUTTON 40 60 4005 4007 1 0 1"></textarea></div>
  <div id="right"><div id="stage"></div></div>
</main>
<div id="info">Soldaki script her değişiklikte sağda gerçek MUL görselleriyle çizilir. &lt;...&gt; ifadeleri yer tutucu olarak gösterilir.</div>
<script>
const src = document.getElementById('src');
const stage = document.getElementById('stage');
const pageSel = document.getElementById('pageSel');
const stat = document.getElementById('stat');
let renderTimer = null;
src.addEventListener('input', () => { clearTimeout(renderTimer); renderTimer = setTimeout(render, 250); });
pageSel.addEventListener('change', render);
document.getElementById('showBounds').addEventListener('change', render);

fetch('/dialog/list').then(r => r.ok ? r.json() : []).then(names => {
  const sel = document.getElementById('dlgList');
  names.sort().forEach(n => { const o = document.createElement('option'); o.value = n; o.textContent = n; sel.appendChild(o); });
}).catch(() => {});

function loadDialog() {
  const name = document.getElementById('dlgList').value;
  if (!name) return;
  fetch('/dialog/source?name=' + encodeURIComponent(name))
    .then(r => r.ok ? r.text() : Promise.reject())
    .then(t => { src.value = t; render(); })
    .catch(() => { stat.textContent = 'kaynak okunamadı'; });
}

// <...> ifadelerini yer tutucuya çevir; koordinat alanında 0 say.
function stripExpr(s) { return s.replace(/<[^>]*>/g, '‹expr›'); }
function isExpr(s) { return s.includes('<'); }

function coordResolve(tok, cur) {
  // ClientDialogHandler.ResolveDialogCoord aynası:
  //  N → row=N, sonuç N | +N/-N → row±N (row değişmez) | *N → row+=N, sonuç row
  if (isExpr(tok)) return { v: 0, bad: true };
  tok = tok.trim();
  let n = 0, mode = 'abs';
  if (tok.startsWith('+')) { mode = 'rel'; n = parseInt(tok.slice(1)); }
  else if (tok.startsWith('*')) { mode = 'step'; n = parseInt(tok.slice(1)); }
  else if (tok.startsWith('-')) { mode = 'rel'; n = -parseInt(tok.slice(1)); }
  else n = parseInt(tok);
  if (isNaN(n)) return { v: 0, bad: true };
  if (mode === 'abs') { cur.row = n; return { v: n }; }
  if (mode === 'rel') { return { v: cur.row + n }; }
  cur.row += n; return { v: cur.row };
}

function tokenize(arg) {
  // <...> grupları tek token sayılır
  const out = []; let cur = ''; let depth = 0;
  for (const ch of arg) {
    if (ch === '<') depth++;
    if (ch === '>') depth = Math.max(0, depth - 1);
    if ((ch === ' ' || ch === '\t') && depth === 0) { if (cur) { out.push(cur); cur = ''; } }
    else cur += ch;
  }
  if (cur) out.push(cur);
  return out;
}

function gumpUrl(id) { return '/gump/' + id + '.png'; }

function el(tag, cls, x, y) {
  const e = document.createElement(tag);
  e.className = 'ctl' + (cls ? ' ' + cls : '');
  e.style.left = x + 'px'; e.style.top = y + 'px';
  return e;
}

function addImg(x, y, id, w, h, title) {
  const img = el('img', '', x, y);
  img.src = gumpUrl(id);
  img.title = title || ('gump 0x' + (+id).toString(16));
  if (w) img.style.width = w + 'px';
  if (h) img.style.height = h + 'px';
  img.onerror = () => {
    const ph = el('div', 'ph', x, y);
    ph.style.width = (w || 40) + 'px'; ph.style.height = (h || 20) + 'px';
    ph.textContent = 'gump ' + id;
    img.replaceWith(ph);
  };
  stage.appendChild(img);
  return img;
}

function addResizePic(x, y, id, w, h) {
  // UO 9-parça: id..id+8 (köşeler doğal, kenarlar/orta tile).
  const box = el('div', '', x, y);
  box.style.width = w + 'px'; box.style.height = h + 'px';
  const corner = new Image();
  corner.onload = () => {
    const cw = corner.naturalWidth, chh = corner.naturalHeight;
    const midW = Math.max(0, w - cw * 2), midH = Math.max(0, h - chh * 2);
    const cells = [
      [0, 0, cw, chh, id], [cw, 0, midW, chh, id + 1], [cw + midW, 0, cw, chh, id + 2],
      [0, chh, cw, midH, id + 3], [cw, chh, midW, midH, id + 4], [cw + midW, chh, cw, midH, id + 5],
      [0, chh + midH, cw, chh, id + 6], [cw, chh + midH, midW, chh, id + 7], [cw + midW, chh + midH, cw, chh, id + 8],
    ];
    for (const [cx, cy, cwd, cht, cid] of cells) {
      if (cwd <= 0 || cht <= 0) continue;
      const d = el('div', 'tiled', cx, cy);
      d.style.width = cwd + 'px'; d.style.height = cht + 'px';
      d.style.backgroundImage = 'url(' + gumpUrl(cid) + ')';
      box.appendChild(d);
    }
  };
  corner.onerror = () => {
    box.className += ' ph'; box.textContent = 'resizepic ' + id;
  };
  corner.src = gumpUrl(id);
  stage.appendChild(box);
}

function hueColor(h) {
  if (isExpr(String(h))) return '#9fd3ff';
  h = parseInt(h) || 0;
  if (h === 0) return '#e8e8e8';
  // kaba yaklaşıklama: hue halkasına dağıt (gerçek hues.mul v2'de)
  const hh = (h * 137) % 360;
  return 'hsl(' + hh + ',55%,72%)';
}

function render() {
  stage.innerHTML = '';
  const wantPage = pageSel.value;
  const showBounds = document.getElementById('showBounds').checked;
  const lines = src.value.split(/\r?\n/);
  let curPage = 0, originX = 0, originY = 0;
  const cx = { row: 0 }, cy = { row: 0 };
  const pages = new Set([0]);
  let count = 0, skipped = 0;

  for (let raw of lines) {
    let line = raw.trim();
    if (!line || line.startsWith('//') || line.startsWith('[')) continue;
    const sp = line.search(/[ \t=]/);
    let verb = (sp < 0 ? line : line.slice(0, sp)).toUpperCase();
    let arg = sp < 0 ? '' : line.slice(sp + 1).trim();
    if (verb === 'PAGE') { curPage = parseInt(arg) || 0; pages.add(curPage); continue; }
    if (['IF','ELSEIF','ELIF','ELSE','ENDIF','FOR','ENDFOR','FORINSTANCES','WHILE','ENDWHILE'].includes(verb)) { skipped++; continue; }
    if (verb.startsWith('LOCAL.') || verb.startsWith('REF') || verb === 'ARGS' || verb.includes('CTAG')) { skipped++; continue; }

    const visible = wantPage === 'all' || curPage === 0 || curPage === parseInt(wantPage);
    const t = tokenize(arg);

    if (verb === 'DORIGIN') {
      originX = isExpr(t[0]||'') ? 0 : parseInt(t[0]) || 0;
      originY = isExpr(t[1]||'') ? 0 : parseInt(t[1]) || 0;
      cx.row = 0; cy.row = 0;
      continue;
    }
    if (!visible) continue;

    const X = i => coordResolve(t[i] || '0', cx).v + originX;
    const Y = i => coordResolve(t[i] || '0', cy).v + originY;
    const N = i => isExpr(t[i]||'') ? 0 : parseInt(t[i]) || 0;
    const text = i => stripExpr(t.slice(i).join(' '));

    try {
      switch (verb) {
        case 'RESIZE':      addResizePic(X(0), Y(1), 9200, N(2), N(3)); break;
        case 'RESIZEPIC':   addResizePic(X(0), Y(1), N(2), N(3), N(4)); break;
        case 'GUMPPIC': case 'GUMPIC': addImg(X(0), Y(1), N(2)); break;
        case 'GUMPPICTILED': {
          const d = el('div', 'tiled', X(0), Y(1));
          d.style.width = N(2) + 'px'; d.style.height = N(3) + 'px';
          d.style.backgroundImage = 'url(' + gumpUrl(N(4)) + ')';
          stage.appendChild(d); break;
        }
        case 'BUTTON':      addImg(X(0), Y(1), N(2), 0, 0, 'button id=' + N(6) + ' page=' + N(5)); break;
        case 'CHECKBOX': case 'RADIO': addImg(X(0), Y(1), N(2)); break;
        case 'TILEPIC': case 'TILEPICHUE': {
          const d = el('div', 'ph', X(0), Y(1));
          d.style.width = '44px'; d.style.height = '44px';
          d.textContent = 'tile 0x' + N(2).toString(16);
          stage.appendChild(d); break;
        }
        case 'DTEXT': case 'TEXT': {
          const d = el('div', 'txt', X(0), Y(1));
          d.style.color = hueColor(t[2]);
          d.innerHTML = escapeHtml(text(3)); stage.appendChild(d); break;
        }
        case 'DCROPPEDTEXT': case 'CROPPEDTEXT': {
          const d = el('div', 'txt crop', X(0), Y(1));
          d.style.width = N(2) + 'px'; d.style.height = N(3) + 'px';
          d.style.color = hueColor(t[4]);
          d.innerHTML = escapeHtml(text(5)); stage.appendChild(d); break;
        }
        case 'DHTMLGUMP': case 'HTMLGUMP': {
          const d = el('div', 'html', X(0), Y(1));
          d.style.width = N(2) + 'px'; d.style.height = N(3) + 'px';
          if (N(4)) { d.style.background = '#ece5cf'; d.style.border = '2px ridge #b9a'; }
          else d.style.color = '#eee';
          d.innerHTML = escapeHtml(text(6)); stage.appendChild(d); break;
        }
        case 'XMFHTMLGUMP': case 'XMFHTMLGUMPCOLOR': {
          const d = el('div', 'html', X(0), Y(1));
          d.style.width = N(2) + 'px'; d.style.height = N(3) + 'px';
          d.style.color = '#cdf'; d.textContent = '[cliloc ' + t[4] + ']';
          stage.appendChild(d); break;
        }
        case 'DTEXTENTRY': case 'TEXTENTRY': case 'DTEXTENTRYLIMITED': case 'TEXTENTRYLIMITED': {
          const d = el('div', 'entry', X(0), Y(1));
          d.style.width = N(2) + 'px'; d.style.height = N(3) + 'px';
          d.textContent = stripExpr(t.slice(7).join(' ')) || ' ';
          stage.appendChild(d); break;
        }
        case 'CHECKERTRANS': {
          const d = el('div', '', X(0), Y(1));
          d.style.width = N(2) + 'px'; d.style.height = N(3) + 'px';
          d.style.background = 'rgba(0,0,0,.45)'; stage.appendChild(d); break;
        }
        case 'TOOLTIP': case 'NOMOVE': case 'NOCLOSE': case 'NODISPOSE': case 'GROUP': break;
        default: skipped++; continue;
      }
      count++;
      if (showBounds) {
        const last = stage.lastElementChild;
        if (last) last.style.outline = '1px solid rgba(120,200,255,.45)';
      }
    } catch (e) { skipped++; }
  }

  // sayfa seçicisini güncelle (seçimi koru)
  const sel = pageSel.value;
  pageSel.innerHTML = '<option value="all">tümü</option>' +
    [...pages].sort((a,b)=>a-b).map(p => '<option value="'+p+'">'+p+'</option>').join('');
  pageSel.value = [...pageSel.options].some(o => o.value === sel) ? sel : 'all';

  stat.textContent = count + ' kontrol çizildi' + (skipped ? ', ' + skipped + ' satır atlandı (akış/ifade)' : '');
}

function escapeHtml(s) {
  return s.replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/>/g,'&gt;')
          .replace(/‹expr›/g, '<span class="expr">&lt;expr&gt;</span>');
}

render();
</script>
</body>
</html>
""";
}
