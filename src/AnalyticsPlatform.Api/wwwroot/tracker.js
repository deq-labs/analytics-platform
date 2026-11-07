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
 * Standard (auto-tracked) events are not instrumented yet -- next step.
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

  // ---- public API --------------------------------------------------------------

  function toStringMap(obj) {
    if (!obj) return undefined;
    var out = {};
    Object.keys(obj).forEach(function (k) { out[k] = String(obj[k]); });
    return out;
  }

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
