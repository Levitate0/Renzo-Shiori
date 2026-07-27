namespace RenzoWindows;

/// <summary>
/// Web-side assets the shell injects: the bridge shim (wraps the WebView2 host
/// object as <c>window.__RenzoWindows</c>, matching the shared frontend's native
/// contract) and the bundled offline reader (shown when the server is
/// unreachable but chapters are saved).
/// </summary>
internal static class NativeAssets
{
    /// <summary>
    /// Injected at document creation on every page (server UI and the offline
    /// reader). Defines a synchronous <c>window.__RenzoWindows</c> over the
    /// <c>renzoNative</c> host object, and relays posted web messages to the DOM
    /// events the frontend adapter listens for (folderpicked / netchange /
    /// download).
    /// </summary>
    public const string BridgeShim = """
(function () {
  try {
    var wv = window.chrome && window.chrome.webview;
    if (!wv || !wv.hostObjects) return;
    if (!window.__RenzoWindows) {
      var s = wv.hostObjects.sync.renzoNative;
      var nz = function (v) { return (v === undefined) ? null : v; };
      window.__RenzoWindows = {
        writeFileB64: function (p, b) { s.WriteFileB64(p, b); },
        readFileB64: function (p) { var v = s.ReadFileB64(p); return v == null ? "" : v; },
        deletePath: function (p) { s.DeletePath(p); },
        exists: function (p) { return !!s.Exists(p); },
        kvGet: function (k) { return nz(s.KvGet(k)); },
        kvSet: function (k, v) { s.KvSet(k, v); },
        isOnline: function () { return !!s.IsOnline(); },
        pickFolder: function () { s.PickFolder(); },
        getFolder: function () { return nz(s.GetFolder()); },
        reconnect: function () { s.Reconnect(); },
        enqueueDownload: function (payload) { s.EnqueueDownload(payload); }
      };
    }
    wv.addEventListener('message', function (e) {
      var d = e.data;
      if (!d || !d.channel) return;
      try { window.dispatchEvent(new CustomEvent(d.channel, { detail: d.detail })); } catch (_) {}
    });
  } catch (_) {}
})();
""";

    /// <summary>
    /// Bundled offline reader — the desktop counterpart to the Android
    /// assets/offline/index.html. Covers grid → series detail → reader, all from
    /// the local manifest, with a Reconnect button that re-loads the server.
    /// </summary>
    public const string OfflineReaderHtml = """
<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8" />
<meta name="viewport" content="width=device-width, initial-scale=1" />
<title>Renzo Shiori — Offline</title>
<style>
  :root { color-scheme: dark; --bg:#0a0a0a; --card:#141414; --border:#262626; --fg:#fafafa; --muted:#a1a1aa; --accent:#e11d48; }
  * { box-sizing: border-box; }
  html, body { margin:0; background:var(--bg); color:var(--fg);
    font-family: system-ui,-apple-system,Segoe UI,Roboto,sans-serif; }
  header { position:sticky; top:0; z-index:10; display:flex; align-items:center; gap:12px;
    padding: 12px 16px; background:#0a0a0acc; backdrop-filter:blur(10px); border-bottom:1px solid var(--border); }
  header .brand { display:flex; align-items:center; gap:8px; flex:1; min-width:0; }
  header h1 { font-size:15px; font-weight:600; margin:0; white-space:nowrap; overflow:hidden; text-overflow:ellipsis; }
  header .badge { font-size:10px; font-weight:700; letter-spacing:.06em; color:var(--muted);
    border:1px solid var(--border); border-radius:999px; padding:2px 8px; text-transform:uppercase; }
  button { font:inherit; color:inherit; background:var(--card); border:1px solid #3f3f46;
    border-radius:8px; padding:8px 12px; cursor:pointer; display:inline-flex; align-items:center; gap:6px; }
  button:hover { background:#1c1c1c; }
  .reconnect { background:var(--accent); border-color:var(--accent); color:#fff; font-weight:600; }
  .iconbtn { padding:8px 12px; }
  main { padding:16px 24px; padding-bottom:40px; max-width:1100px; margin:0 auto; }
  .grid { display:grid; grid-template-columns:repeat(auto-fill,minmax(160px,1fr)); gap:16px; }
  .card { cursor:pointer; }
  .cover { position:relative; width:100%; aspect-ratio:2/3; border-radius:12px; overflow:hidden;
    background:linear-gradient(135deg,#1c1c22,#0e0e12); border:1px solid var(--border); }
  .cover img { width:100%; height:100%; object-fit:cover; display:block; }
  .cover .ph { position:absolute; inset:0; display:flex; align-items:center; justify-content:center; color:#52525b; font-size:28px; }
  .cover .count { position:absolute; right:8px; top:8px; background:#000000b3; color:#fff;
    font-size:11px; font-weight:600; padding:3px 8px; border-radius:999px; }
  .card .t { margin-top:8px; font-size:13px; font-weight:600; line-height:1.3;
    display:-webkit-box; -webkit-line-clamp:2; -webkit-box-orient:vertical; overflow:hidden; }
  .card .s { font-size:11px; color:var(--muted); margin-top:2px; }
  .hero { display:flex; gap:16px; margin-bottom:16px; }
  .hero .cover { width:130px; flex:0 0 130px; }
  .hero h2 { margin:0 0 4px; font-size:20px; }
  .hero .by { color:var(--muted); font-size:12px; }
  .hero .desc { color:#c4c4c8; font-size:13px; margin-top:8px; line-height:1.5;
    display:-webkit-box; -webkit-line-clamp:6; -webkit-box-orient:vertical; overflow:hidden; }
  .chapter { display:flex; align-items:center; gap:10px; padding:12px 14px; margin-bottom:8px;
    background:var(--card); border:1px solid var(--border); border-radius:10px; cursor:pointer; }
  .chapter:hover { background:#1c1c1c; }
  .chapter .n { font-weight:600; }
  .chapter .meta { font-size:12px; color:#71717a; margin-left:auto; }
  .empty { text-align:center; color:#71717a; padding:64px 24px; line-height:1.6; }
  #readerPages { max-width:900px; margin:0 auto; }
  #readerPages img { display:block; width:100%; height:auto; }
  .spinner { text-align:center; padding:40px; color:#71717a; }
  .hidden { display:none !important; }
  .section-label { font-size:11px; font-weight:600; color:var(--muted); text-transform:uppercase;
    letter-spacing:.05em; margin:0 0 10px; }
</style>
</head>
<body>
<header>
  <button id="backBtn" class="iconbtn hidden" aria-label="Back">&#8249;</button>
  <div class="brand"><h1 id="title">Offline library</h1><span class="badge">Offline</span></div>
  <button id="reconnectBtn" class="reconnect">Reconnect</button>
</header>
<main>
  <div id="library"></div>
  <div id="series" class="hidden"></div>
  <div id="reader" class="hidden"><div id="readerPages"></div></div>
</main>
<script>
(function () {
  var A = window.__RenzoWindows;
  var libraryEl = document.getElementById('library');
  var seriesEl = document.getElementById('series');
  var readerEl = document.getElementById('reader');
  var readerPagesEl = document.getElementById('readerPages');
  var titleEl = document.getElementById('title');
  var backBtn = document.getElementById('backBtn');
  var view = 'library';
  var currentSeriesId = null;

  document.getElementById('reconnectBtn').addEventListener('click', function () {
    try { A && A.reconnect(); } catch (e) {}
  });
  backBtn.addEventListener('click', function () {
    if (view === 'reader') showSeries(currentSeriesId);
    else showLibrary();
  });

  function esc(s){ return String(s == null ? '' : s).replace(/[&<>"]/g, function(c){
    return {'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;'}[c]; }); }
  function mime(p){ var e=(p.split('.').pop()||'').toLowerCase();
    return e==='png'?'image/png':e==='webp'?'image/webp':e==='gif'?'image/gif':e==='avif'?'image/avif':'image/jpeg'; }
  function dataUri(path){ if(!path) return null; try { var b=A.readFileB64(path); return b?('data:'+mime(path)+';base64,'+b):null; } catch(e){ return null; } }
  function manifest(){ if(!A) return {series:{},chapters:{}};
    try { var m=JSON.parse(A.kvGet('renzo.offline.manifest.v1')||'{}'); m.series=m.series||{}; m.chapters=m.chapters||{}; return m; }
    catch(e){ return {series:{},chapters:{}}; } }
  function chaptersOf(m, sid){ return Object.keys(m.chapters).map(function(k){return m.chapters[k];})
    .filter(function(c){return c.seriesId===sid;}).sort(function(a,b){return a.chapterNumber-b.chapterNumber;}); }

  function showLibrary(){
    view='library'; currentSeriesId=null;
    libraryEl.classList.remove('hidden'); seriesEl.classList.add('hidden'); readerEl.classList.add('hidden');
    backBtn.classList.add('hidden'); titleEl.textContent='Offline library';
    var m=manifest();
    var counts={}; Object.keys(m.chapters).forEach(function(k){var c=m.chapters[k]; counts[c.seriesId]=(counts[c.seriesId]||0)+1;});
    var series=Object.keys(m.series).map(function(id){return m.series[id];})
      .filter(function(s){return counts[s.seriesId]>0;})
      .sort(function(a,b){return String(a.title).localeCompare(String(b.title));});
    if(!A){ libraryEl.innerHTML='<div class="empty">Open this from the Renzo Shiori app.</div>'; return; }
    if(series.length===0){ libraryEl.innerHTML='<div class="empty">No downloaded series yet.<br>Save some for offline while you are online.</div>'; return; }
    var html='<div class="grid">';
    series.forEach(function(s){
      var cov=dataUri(s.coverPath);
      html+='<div class="card" data-id="'+esc(s.seriesId)+'">'+
        '<div class="cover">'+(cov?('<img src="'+cov+'" alt="">'):'<div class="ph">&#9962;</div>')+
        '<span class="count">'+counts[s.seriesId]+'</span></div>'+
        '<div class="t">'+esc(s.title)+'</div>'+
        '<div class="s">'+counts[s.seriesId]+' chapter'+(counts[s.seriesId]===1?'':'s')+'</div></div>';
    });
    html+='</div>';
    libraryEl.innerHTML=html;
    Array.prototype.forEach.call(libraryEl.querySelectorAll('.card'), function(el){
      el.addEventListener('click', function(){ showSeries(el.getAttribute('data-id')); });
    });
  }

  function showSeries(sid){
    view='series'; currentSeriesId=sid;
    libraryEl.classList.add('hidden'); seriesEl.classList.remove('hidden'); readerEl.classList.add('hidden');
    backBtn.classList.remove('hidden');
    var m=manifest(); var s=m.series[sid]; if(!s){ showLibrary(); return; }
    titleEl.textContent=s.title;
    var cov=dataUri(s.coverPath);
    var chs=chaptersOf(m, sid);
    var html='<div class="hero"><div class="cover">'+(cov?('<img src="'+cov+'" alt="">'):'<div class="ph">&#9962;</div>')+'</div>'+
      '<div><h2>'+esc(s.title)+'</h2>'+
      (s.author?('<div class="by">by '+esc(s.author)+'</div>'):'')+
      (s.description?('<div class="desc">'+esc(s.description)+'</div>'):'')+'</div></div>';
    html+='<p class="section-label">'+chs.length+' chapter'+(chs.length===1?'':'s')+' offline</p>';
    chs.forEach(function(c){
      html+='<div class="chapter" data-key="'+esc(c.chapterKey)+'"><span class="n">Ch. '+esc(String(c.chapterNumber))+'</span>'+
        '<span class="meta">'+c.pageCount+' pages</span></div>';
    });
    seriesEl.innerHTML=html;
    Array.prototype.forEach.call(seriesEl.querySelectorAll('.chapter'), function(el){
      el.addEventListener('click', function(){ openChapter(el.getAttribute('data-key')); });
    });
    window.scrollTo(0,0);
  }

  function markRead(seriesId, chapterNumber){
    if(!A) return;
    try {
      var list = JSON.parse(A.kvGet('renzo.offline.pendingReads') || '[]');
      var dup = list.some(function(x){ return x.seriesId===seriesId && x.chapterNumber===chapterNumber; });
      if(!dup){ list.push({seriesId:seriesId, chapterNumber:chapterNumber}); A.kvSet('renzo.offline.pendingReads', JSON.stringify(list)); }
    } catch(e){}
  }

  function openChapter(key){
    var m=manifest(); var c=m.chapters[key]; if(!c) return;
    view='reader';
    libraryEl.classList.add('hidden'); seriesEl.classList.add('hidden'); readerEl.classList.remove('hidden');
    backBtn.classList.remove('hidden');
    titleEl.textContent=(m.series[c.seriesId]?m.series[c.seriesId].title:c.seriesTitle)+' &middot; Ch. '+c.chapterNumber;
    readerPagesEl.innerHTML='<div class="spinner">Loading...</div>';
    var frag=document.createDocumentFragment();
    for(var i=0;i<c.pagePaths.length;i++){
      var uri=dataUri(c.pagePaths[i]); if(!uri) continue;
      var img=document.createElement('img'); img.loading='lazy'; img.src=uri; frag.appendChild(img);
    }
    readerPagesEl.innerHTML=''; readerPagesEl.appendChild(frag);
    window.scrollTo(0,0);
    var marked=false;
    function checkRead(){
      if(marked) return;
      var atEnd = (window.scrollY + window.innerHeight) >= (document.body.scrollHeight - 400);
      if(atEnd){ marked=true; markRead(c.seriesId, c.chapterNumber); window.removeEventListener('scroll', checkRead); }
    }
    window.addEventListener('scroll', checkRead, {passive:true});
    setTimeout(checkRead, 300);
  }

  showLibrary();
})();
</script>
</body>
</html>
""";
}
