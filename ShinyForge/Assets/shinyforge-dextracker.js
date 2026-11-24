// ShinyForge <-> DexTracker bridge. ShinyForge serves DexTracker on 127.0.0.1 and adds this script before DexTracker's
// own code. DexTracker keeps its savefile in localStorage; this script makes ShinyForge's file (Documents\ShinyForge\...
// DexTracker\savefile.json, or next to a portable copy) the real copy:
//  - on load the savefile comes from ShinyForge (synchronously, before DexTracker reads localStorage)
//  - every time DexTracker saves, the savefile is sent back to ShinyForge
//  - when ShinyForge marks Pokémon it found in your saves, the page reloads to show them
(function () {
  'use strict';
  var KEY = 'dextracker.savefile.v1';
  var PREF_KEY = 'dextracker.ui.v1';
  var rev = null;
  var pending = null, timer = null, sending = false, reloading = false;
  var setItem = Storage.prototype.setItem;

  function request(method, url, body, sync) {
    var x = new XMLHttpRequest();
    x.open(method, url, !sync);
    x.setRequestHeader('X-ShinyForge', '1');
    if (rev !== null) x.setRequestHeader('X-Base-Rev', rev);
    if (body != null) x.setRequestHeader('Content-Type', 'application/json');
    return x;
  }

  function banner(text) {
    var b = document.getElementById('shinyforge-banner');
    if (!b) {
      b = document.createElement('div');
      b.id = 'shinyforge-banner';
      b.style.cssText = 'position:fixed;left:50%;bottom:16px;transform:translateX(-50%);z-index:99999;background:#1f2937;color:#fff;' +
        'padding:8px 14px;border-radius:8px;font:13px system-ui,sans-serif;box-shadow:0 4px 14px rgba(0,0,0,.3)';
      (document.body || document.documentElement).appendChild(b);
    }
    b.textContent = text;
  }

  function reload(why) {
    if (reloading) return;
    reloading = true;
    try { banner(why); } catch (e) {}
    setTimeout(function () { location.reload(); }, 600);
  }

  // ---- 1. load the savefile from ShinyForge before DexTracker starts
  try {
    var x = request('GET', '/shinyforge/savefile', null, true);
    x.send();
    if (x.status === 200) {
      var r = JSON.parse(x.responseText);
      rev = r.rev;
      if (r.data) setItem.call(localStorage, KEY, JSON.stringify(r.data));
      else if (localStorage.getItem(KEY)) pending = localStorage.getItem(KEY); // first run: keep what this browser already had
    }
  } catch (e) { console.warn('ShinyForge: could not load the savefile', e); }

  // ?dex=FRLG%20Dex (from a game's page in ShinyForge): open the Box View on that Pokédex
  try {
    var dex = new URLSearchParams(location.search).get('dex');
    if (dex) {
      var prefs = JSON.parse(localStorage.getItem(PREF_KEY) || '{}');
      prefs.tab = 'box';
      prefs.boxDex = dex;
      prefs.boxMode = /^(Nat|Shiny Nat)/.test(dex) ? 'national' : 'regional'; // a game's Pokédex in its own numbering
      setItem.call(localStorage, PREF_KEY, JSON.stringify(prefs));
      history.replaceState(null, '', location.pathname);
    }
  } catch (e) {}

  // ---- 2. send every change back to ShinyForge
  function flush() {
    timer = null;
    if (pending === null || sending || reloading) return;
    var body = pending;
    pending = null;
    sending = true;
    var x = request('PUT', '/shinyforge/savefile', body, false);
    x.onload = function () {
      sending = false;
      try {
        if (x.status === 200) { rev = JSON.parse(x.responseText).rev; if (pending !== null) flush(); }
        else if (x.status === 409) reload('ShinyForge updated your dex - reloading…');
        else { pending = pending === null ? body : pending; banner('ShinyForge didn\'t save the last change (' + x.status + ').'); }
      } catch (e) { console.warn(e); }
    };
    x.onerror = function () {
      sending = false;
      if (pending === null) pending = body;
      banner('ShinyForge isn\'t running - your changes are kept here and saved when it is.');
      timer = setTimeout(flush, 5000);
    };
    x.send(body);
  }

  Storage.prototype.setItem = function (k, v) {
    setItem.call(this, k, v);
    if (this === localStorage && k === KEY) {
      pending = String(v);
      if (timer) clearTimeout(timer);
      timer = setTimeout(flush, 300);
    }
  };
  if (pending !== null) timer = setTimeout(flush, 300);

  window.addEventListener('pagehide', function () {
    if (pending === null) return;
    try {
      var x = request('PUT', '/shinyforge/savefile', pending, true);
      x.send(pending);
      pending = null;
    } catch (e) {}
  });

  // ---- 3. notice ShinyForge's own changes (Pokémon found in your saves)
  setInterval(function () {
    if (sending || pending !== null || reloading) return;
    var x = request('GET', '/shinyforge/rev', null, false);
    x.onload = function () {
      try {
        var r = JSON.parse(x.responseText).rev;
        if (rev !== null && r !== rev && !sending && pending === null) reload('ShinyForge added Pokémon from your saves - reloading…');
      } catch (e) {}
    };
    x.send();
  }, 4000);
})();
