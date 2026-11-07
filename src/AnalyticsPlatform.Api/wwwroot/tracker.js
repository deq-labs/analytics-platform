/*!
 * Analytics Platform tracker -- AGPL-3.0
 *
 * Usage (on the tracked site):
 *   <script defer src="https://YOUR-API/tracker.js" data-site-id="1"></script>
 *
 * Optional attributes:
 *   data-wait-for-consent   Persistent-mode sites only: don't store a visitor ID
 *                           until analyticsPlatform.consent() is called. Until
 *                           then the server uses the cookie-less hash.
 *
 * JavaScript API:
 *   analyticsPlatform.track("signup_click", { plan: "pro" })   custom event
 *   analyticsPlatform.consent()          allow storing a visitor ID (persistent mode)
 *   analyticsPlatform.revokeConsent()    delete the stored visitor ID
 *
 * Standard (auto-tracked) events -- each only sent if enabled for the site:
 *   scroll           25/50/75/90% of the page, once each per page   {depth}
 *   outbound_click   link to another hostname                       {url, domain}
 *   file_download    link to a file, or with a download attribute   {url, extension}
 *   form_start       first input into a form                        {form_id, form_name, form_action}
 *   form_submit      form submitted                                 {form_id, form_name, form_action}
 *   video_start / video_progress (25/50/75) / video_complete
 *                    native <video> only, not YouTube/Vimeo iframes {video_url, video_title, percent}
 * Form field values are never read or sent.
 */
(function () {
  "use strict";

  var script = document.currentScript;
  if (!script) return;

  var siteId = Number(script.getAttribute("data-site-id"));
  if (!siteId) {
    console.warn("[analytics] tracker.js needs a data-site-id attribute");
    return;
  }

  // Send events to whichever server this script was loaded from.
  var api = new URL(script.src).origin;
  var waitForConsent = script.hasAttribute("data-wait-for-consent");
  var STORAGE_KEY = "ap_visitor_id";

  // Defaults until the site's config arrives (or if fetching it fails).
  var config = { visitorIdMode: "hash", standardEvents: [] };
  var ready = false;
  var queue = [];
  var lastPage = null;

  // ---- visitor ID (persistent mode only) -----------------------------------

  function newUuid() {
    if (window.crypto && crypto.randomUUID) return crypto.randomUUID();
    // Fallback for old browsers / non-HTTPS pages.
    var b = crypto.getRandomValues(new Uint8Array(16));
    b[6] = (b[6] & 0x0f) | 0x40;
    b[8] = (b[8] & 0x3f) | 0x80;
    var h = Array.prototype.map.call(b, function (x) { return (x + 0x100).toString(16).slice(1); }).join("");
    return h.slice(0, 8) + "-" + h.slice(8, 12) + "-" + h.slice(12, 16) + "-" + h.slice(16, 20) + "-" + h.slice(20);
  }

  // Returns the stored ID, creating it if allowed. Returns undefined in hash
  // mode, before consent, or if storage is blocked -- the server then falls
  // back to the cookie-less hash.
  function visitorId() {
    if (config.visitorIdMode !== "persistent") return undefined;
    try {
      var id = localStorage.getItem(STORAGE_KEY);
      if (!id && !waitForConsent) {
        id = newUuid();
        localStorage.setItem(STORAGE_KEY, id);
      }
      return id || undefined;
    } catch (e) {
      return undefined;
    }
  }

  // ---- sending ---------------------------------------------------------------

  function send(payload) {
    // Standard events are captured from the start but only sent if the site
    // has them turned on. (Sending happens after the config has loaded.)
    if (payload.type === "standard" && config.standardEvents.indexOf(payload.name) === -1) return;
    payload.visitorId = visitorId();
    // text/plain keeps this a "simple" cross-site request (no CORS preflight).
    // keepalive lets the request finish even if the page is being closed.
    fetch(api + "/api/events", {
      method: "POST",
      body: JSON.stringify(payload),
      headers: { "Content-Type": "text/plain" },
      keepalive: true,
      credentials: "omit",
    }).catch(function () { /* analytics must never break the site */ });
  }

  function enqueue(type, name, properties, referrer) {
    var payload = {
      siteId: siteId,
      type: type,
      name: name || undefined,
      url: location.href,
      referrer: referrer || undefined,
      screenWidth: screen.width,
      screenHeight: screen.height,
      properties: properties,
    };
    if (ready) send(payload);
    else queue.push(payload);
  }

  // ---- pageviews (including single-page apps) -------------------------------

  function pageview() {
    // Ignore #anchor changes and repeated calls for the same page.
    var page = location.origin + location.pathname + location.search;
    if (page === lastPage) return;
    // Only the first pageview has an external referrer; later ones are
    // in-app navigations.
    var referrer = lastPage === null ? document.referrer : undefined;
    lastPage = page;
    scrollDepthsSent = {}; // new page, so scroll depth starts over
    enqueue("pageview", undefined, undefined, referrer);
  }

  // React/Vue/etc. change pages with history.pushState instead of a full page
  // load, so wrap it to notice those navigations. popstate = back/forward.
  var originalPushState = history.pushState;
  history.pushState = function () {
    var result = originalPushState.apply(this, arguments);
    pageview();
    return result;
  };
  window.addEventListener("popstate", pageview);

  // ---- standard (auto-tracked) events ------------------------------------------

  // The API only accepts string property values up to 512 characters (a longer
  // one would get the whole event rejected), so convert and trim. Empty values
  // are dropped.
  function toStringMap(obj) {
    if (!obj) return undefined;
    var out = {};
    Object.keys(obj).forEach(function (k) {
      var v = obj[k];
      if (v === undefined || v === null || v === "") return;
      out[k] = String(v).slice(0, 500);
    });
    return out;
  }

  function trackStandard(name, properties) {
    enqueue("standard", name, toStringMap(properties));
  }

  // Scroll depth. Listening to "scroll" fires dozens of times per second, so
  // the check runs at most once per animation frame.
  var SCROLL_DEPTHS = [25, 50, 75, 90];
  var scrollDepthsSent = {};
  var scrollCheckPending = false;

  function checkScrollDepth() {
    scrollCheckPending = false;
    var scrollable = document.documentElement.scrollHeight - window.innerHeight;
    if (scrollable <= 0) return; // page doesn't scroll
    var percent = (window.scrollY / scrollable) * 100;
    SCROLL_DEPTHS.forEach(function (depth) {
      if (percent >= depth && !scrollDepthsSent[depth]) {
        scrollDepthsSent[depth] = true;
        trackStandard("scroll", { depth: depth });
      }
    });
  }

  window.addEventListener("scroll", function () {
    if (scrollCheckPending) return;
    scrollCheckPending = true;
    requestAnimationFrame(checkScrollDepth);
  }, { passive: true });

  // Link clicks: downloads and outbound links. One listener on the whole
  // document ("event delegation") catches clicks on every link, including
  // links added to the page later by React etc. `true` = capture phase, so we
  // see the click even if the site's own code stops it from bubbling.
  var FILE_EXTENSION = /\.(pdf|zip|rar|7z|gz|tar|dmg|exe|msi|pkg|apk|iso|csv|xlsx?|docx?|pptx?|txt|rtf|odt|ods|odp|epub|mp3|wav|mp4|mov|avi|mkv)$/i;

  function onLinkClick(e) {
    if (e.type === "auxclick" && e.button !== 1) return; // only middle-click, not right-click
    var link = e.target && e.target.closest ? e.target.closest("a[href]") : null;
    if (!link) return;

    var url;
    try { url = new URL(link.href, location.href); } catch (err) { return; }

    var extension = FILE_EXTENSION.exec(url.pathname);
    if (extension || link.hasAttribute("download")) {
      trackStandard("file_download", {
        url: url.href,
        extension: extension ? extension[1].toLowerCase() : "",
      });
    } else if ((url.protocol === "http:" || url.protocol === "https:") && url.hostname !== location.hostname) {
      trackStandard("outbound_click", { url: url.href, domain: url.hostname });
    }
  }

  document.addEventListener("click", onLinkClick, true);
  document.addEventListener("auxclick", onLinkClick, true);

  // Forms. Only *which* form -- never what was typed into it.
  // A WeakSet remembers forms we've already reported without keeping removed
  // forms in memory.
  var startedForms = new WeakSet();

  function formProperties(form) {
    // getAttribute, because form.action/form.name can be hidden by an
    // <input name="action"> inside the form.
    return {
      form_id: form.getAttribute("id"),
      form_name: form.getAttribute("name"),
      form_action: form.getAttribute("action"),
    };
  }

  document.addEventListener("input", function (e) {
    var form = e.target && e.target.form; // inputs/selects/textareas know their form
    if (!form || startedForms.has(form)) return;
    startedForms.add(form);
    trackStandard("form_start", formProperties(form));
  }, true);

  document.addEventListener("submit", function (e) {
    if (e.target && e.target.tagName === "FORM") trackStandard("form_submit", formProperties(e.target));
  }, true);

  // Native <video>. Media events don't bubble, but capture-phase listeners on
  // the document still see them.
  var VIDEO_PROGRESS = [25, 50, 75];
  var videoState = new WeakMap();

  function isVideo(el) { return el && el.tagName === "VIDEO"; }

  function stateFor(video) {
    var s = videoState.get(video);
    if (!s) { s = { started: false, completed: false, progress: {} }; videoState.set(video, s); }
    return s;
  }

  function videoProperties(video, percent) {
    var src = video.currentSrc || video.getAttribute("src") || "";
    return {
      video_url: src.split(/[?#]/)[0], // query strings often hold access tokens
      video_title: video.getAttribute("title") || video.getAttribute("aria-label"),
      percent: percent,
    };
  }

  document.addEventListener("play", function (e) {
    if (!isVideo(e.target)) return;
    var s = stateFor(e.target);
    if (s.started) return;
    s.started = true;
    trackStandard("video_start", videoProperties(e.target));
  }, true);

  document.addEventListener("timeupdate", function (e) {
    var video = e.target;
    if (!isVideo(video) || !isFinite(video.duration) || video.duration <= 0) return; // skip live streams
    var s = stateFor(video);
    var percent = (video.currentTime / video.duration) * 100;
    VIDEO_PROGRESS.forEach(function (p) {
      if (percent >= p && !s.progress[p]) {
        s.progress[p] = true;
        trackStandard("video_progress", videoProperties(video, p));
      }
    });
  }, true);

  document.addEventListener("ended", function (e) {
    if (!isVideo(e.target)) return;
    var s = stateFor(e.target);
    if (s.completed) return;
    s.completed = true;
    trackStandard("video_complete", videoProperties(e.target, 100));
  }, true);

  // ---- public API --------------------------------------------------------------

  window.analyticsPlatform = {
    track: function (name, properties) {
      enqueue("custom", name, toStringMap(properties));
    },
    consent: function () {
      waitForConsent = false;
      visitorId(); // creates and stores the ID now
    },
    revokeConsent: function () {
      waitForConsent = true;
      try { localStorage.removeItem(STORAGE_KEY); } catch (e) { /* ignore */ }
    },
  };

  // ---- start: load site config, then flush anything queued ---------------------

  pageview();

  fetch(api + "/api/sites/" + siteId + "/config", { credentials: "omit" })
    .then(function (r) { return r.ok ? r.json() : null; })
    .then(function (c) { if (c) config = c; })
    .catch(function () { /* keep defaults */ })
    .then(function () {
      ready = true;
      queue.splice(0).forEach(send);
    });
})();
