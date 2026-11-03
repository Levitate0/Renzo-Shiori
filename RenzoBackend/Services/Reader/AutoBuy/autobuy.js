// Generic "buy this chapter" helper, injected into the chapter page by
// ChapterAutoBuyService. It knows no site: it reads what any coin site shows a
// person (a buy/unlock button, a price, a balance, a sign-in prompt) and clicks
// the site's own button. State lives in sessionStorage so it survives the
// reload some sites do after a purchase. Each step calls __rzStep(name).
(function () {
  var KEY = "__rz_autobuy";
  function load() { try { return JSON.parse(sessionStorage.getItem(KEY) || "{}"); } catch (e) { return {}; } }
  function save(s) { try { sessionStorage.setItem(KEY, JSON.stringify(s)); } catch (e) { } }

  function vis(e) {
    var r = e.getBoundingClientRect(); var s = getComputedStyle(e);
    return r.width > 0 && r.height > 0 && s.visibility !== "hidden" && s.display !== "none" && s.opacity !== "0";
  }
  function label(e) {
    return (e.innerText || e.value || e.getAttribute("aria-label") || e.getAttribute("title") || "")
      .replace(/\s+/g, " ").trim();
  }
  function num(t) { var m = /(\d[\d,.]*)/.exec(t || ""); return m ? parseFloat(m[1].replace(/,/g, "")) : null; }

  // Never a single-chapter purchase: real money, coin packs, bundles, top-ups.
  var MONEY = /[$€£¥₩₹]|\b(usd|eur|gbp|paypal|card)\b/i;
  var NOT_ONE_CHAPTER = /\b(all|bundle|packs?|package|season|volume|wallet|top ?up|recharge|subscribe|subscription|vip|membership|pass|gift|earn|shop|store|get( my)? coins|(buy|purchase|get) (more )?coins|download)\b/i;
  var BUY = /\b(unlock|buy|purchase)\b|\bread (now )?for\b/i;
  var SIGN_IN = /\b(log ?in|sign ?in|register|sign ?up)\b/i;
  var BAD_HREF = /\/(coins?|coin-shop|wallet|shop|store|login|signin|sign-in|register|signup|account|premium|subscribe)\b/i;
  var SIGNED_OUT = /\b(log ?in|sign ?in)\b[^.\n|]{0,40}\b(unlock|read|buy|purchase|continue|access|view)\b|\bplease (log|sign) ?in\b|\b(log|sign) ?in (required|first)\b/i;
  var NO_COINS = /\bnot enough\b|\binsufficient\b|\bneed more coins\b|\bdon'?t have enough\b|\blow balance\b|\bbalance is too low\b/i;

  function bodyText() { return document.body ? document.body.innerText : ""; }
  function count(re, t) { var g = new RegExp(re.source, "gi"); return (t.match(g) || []).length; }

  function candidates() {
    var out = [];
    document.querySelectorAll("button,a,[role=button],input[type=submit],input[type=button]").forEach(function (e) {
      if (!vis(e)) return;
      var t = label(e);
      if (!t || t.length > 80 || !BUY.test(t)) return;
      if (MONEY.test(t) || NOT_ONE_CHAPTER.test(t) || SIGN_IN.test(t)) return;
      var href = e.tagName === "A" ? (e.getAttribute("href") || "") : "";
      if (href && href !== "#" && !/^javascript:/i.test(href)) {
        try {
          var u = new URL(href, location.href);
          if (u.origin !== location.origin || BAD_HREF.test(u.pathname)) return;
        } catch (x) { return; }
      }
      var score = 0;
      if (/coin|point|\d/i.test(t)) score += 3;
      if (/chapter|episode/i.test(t)) score += 2;
      if (e.tagName !== "A") score += 1;
      out.push({ e: e, t: t, score: score });
    });
    out.sort(function (a, b) { return b.score - a.score; });
    return out;
  }

  function priceFor(c) {
    if (!c) return null;
    var p = /coin|point/i.test(c.t) ? num(c.t) : null;
    if (p != null) return p;
    // The card the button sits in: "PRICE 100", "100 coins".
    var box = c.e;
    for (var i = 0; i < 5 && box.parentElement; i++) {
      box = box.parentElement;
      var t = label(box);
      if (t.length > 500) break;
      var m = /\bprice\W{0,12}(\d[\d,]*)/i.exec(t) || /(\d[\d,]*)\s*(coins?|points?)\b/i.exec(t);
      if (m) return parseFloat(m[1].replace(/,/g, ""));
    }
    return null;
  }

  function balance() {
    var t = bodyText();
    var m = /\b(?:coin balance|balance|your coins|coins?\s*:)\W{0,15}(\d[\d,]*)/i.exec(t);
    return m ? parseFloat(m[1].replace(/,/g, "")) : null;
  }

  function readable() {
    var n = 0;
    for (var i = 0; i < document.images.length; i++) {
      var im = document.images[i];
      if (im.naturalHeight >= 500 && im.naturalWidth >= 300 && vis(im)) n++;
    }
    return n >= 2;
  }

  function dialogs() {
    return Array.prototype.filter.call(document.querySelectorAll(
      "[role=dialog],[role=alertdialog],[aria-modal=true],dialog[open],.modal,.swal2-popup,[class*=modal],[class*=dialog],[class*=popup]"),
      vis);
  }

  function confirmInDialog() {
    var ds = dialogs();
    for (var i = 0; i < ds.length; i++) {
      var bs = ds[i].querySelectorAll("button,a,[role=button],input[type=submit],input[type=button]");
      for (var j = 0; j < bs.length; j++) {
        var b = bs[j], t = label(b);
        if (!vis(b) || !t || t.length > 60) continue;
        if (/\b(cancel|no|close|back|later)\b/i.test(t) || MONEY.test(t) || NOT_ONE_CHAPTER.test(t) || SIGN_IN.test(t)) continue;
        if (/\b(confirm|yes|unlock|buy|purchase|ok|okay|continue|spend|proceed|use)\b/i.test(t)) { b.click(); return t; }
      }
    }
    return null;
  }

  window.__rzStep = function (step, dry) {
    var s = load();
    if (step === "inspect") {
      var cs = candidates(), best = cs[0] || null, t = bodyText();
      var r = {
        url: location.href,
        // Images alone prove nothing while a buy button is up (covers, ads).
        readable: cs.length === 0 && readable(),
        signedOut: SIGNED_OUT.test(t),
        buttons: cs.slice(0, 4).map(function (c) { return c.t; }),
        price: priceFor(best),
        balance: balance(),
        clicked: false
      };
      if (!dry && !r.signedOut && best &&
          !(r.price != null && r.balance != null && r.balance < r.price)) {
        window.confirm = function () { return true; };
        window.alert = function () { };
        s = { clicked: true, button: best.t, noCoins: count(NO_COINS, t), signedOut: count(SIGNED_OUT, t) };
        save(s);
        best.e.click();
        r.clicked = true;
      } else {
        save({});
      }
      return JSON.stringify(r);
    }
    if (!s.clicked) return "skip";
    var now = bodyText();
    if (count(NO_COINS, now) > (s.noCoins || 0)) {
      var m = new RegExp(".{0,80}(" + NO_COINS.source + ").{0,80}", "i").exec(now.replace(/\n+/g, " "));
      return JSON.stringify({ noCoins: true, text: m ? m[0].trim() : "" });
    }
    if (count(SIGNED_OUT, now) > (s.signedOut || 0)) return "signedOut";
    if (step === "confirm") {
      if (s.confirmed) return null;
      var c = confirmInDialog();
      if (c) { s.confirmed = true; save(s); return "confirmed"; }
      return null;
    }
    // "outcome": a second confirmation can still appear.
    if (!s.confirmed && confirmInDialog()) { s.confirmed = true; save(s); return null; }
    // The button that was clicked is gone and nothing complained: bought, or
    // at least worth the server's own check.
    var still = candidates().some(function (c) { return c.t === s.button; });
    if (!still && dialogs().length === 0) return "gone";
    return null;
  };

  // Sign-in with the saved login, for sites that keep it outside cookies (a
  // token in the page's storage), so the server's session doesn't reach the
  // browser. Fills the site's own form. Never touches a captcha: one on the
  // form ends the attempt.
  var CAPTCHA = "iframe[src*='challenges.cloudflare.com'],.cf-turnstile,.g-recaptcha,.h-captcha,iframe[src*='recaptcha'],iframe[src*='hcaptcha']";
  function passwordField() {
    return Array.prototype.filter.call(document.querySelectorAll("input[type=password]"), vis)[0] || null;
  }
  function setVal(el, v) {
    var d = Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, "value");
    d.set.call(el, v);
    el.dispatchEvent(new Event("input", { bubbles: true }));
    el.dispatchEvent(new Event("change", { bubbles: true }));
  }
  window.__rzLogin = function (step, user, pass) {
    if (step === "open") {
      if (passwordField()) return "form";
      var els = Array.prototype.filter.call(document.querySelectorAll("a,button,[role=button]"), function (e) {
        var t = label(e); return vis(e) && t.length < 40 && /\b(log ?in|sign ?in)\b/i.test(t);
      });
      for (var i = 0; i < els.length; i++) {
        var h = els[i].tagName === "A" ? els[i].getAttribute("href") : null;
        if (h && h.charAt(0) !== "#" && !/^javascript:/i.test(h)) { location.href = els[i].href; return "nav"; }
      }
      if (els[0]) { els[0].click(); return "click"; }
      return "none";
    }
    if (step === "form") return passwordField() ? "ready" : null;
    if (step === "submit") {
      var pw = passwordField();
      if (!pw) return "noform";
      var scope = pw.form || pw.closest("form") || document;
      if (scope.querySelector(CAPTCHA) || document.querySelector(CAPTCHA)) return "captcha";
      var inputs = Array.prototype.filter.call(scope.querySelectorAll("input"), function (i) {
        return vis(i) && i !== pw && /^(text|email|tel|)$/i.test(i.type || "");
      });
      var u = inputs.filter(function (i) {
        return i.type === "email" || /email|user|login|name|account/i.test((i.name || "") + (i.id || "") + (i.autocomplete || "") + (i.placeholder || ""));
      })[0] || inputs[0];
      if (!u) return "nouser";
      setVal(u, user); setVal(pw, pass);
      var btn = Array.prototype.filter.call(scope.querySelectorAll("button,input[type=submit]"), function (b) {
        return vis(b) && (b.type === "submit" || /\b(log ?in|sign ?in|continue|submit)\b/i.test(label(b)));
      })[0];
      if (btn) btn.click(); else if (pw.form) pw.form.requestSubmit(); else return "nosubmit";
      return "submitted";
    }
    if (step === "done") return passwordField() ? null : "done";
    return null;
  };
  return true;
})();
