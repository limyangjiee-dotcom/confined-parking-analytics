/* Shared shell + helpers for the Confined Parking Analytics dashboard */
const API_KEY = "fyp-demo-key-2025";
const CAPACITY = 11000;

const PALETTE = {
  accent: "#38bdf8", violet: "#8b5cf6", green: "#34d399",
  amber: "#fbbf24", rose: "#fb7185", blue: "#60a5fa", teal: "#2dd4bf"
};
const SERIES = [PALETTE.accent, PALETTE.violet, PALETTE.green, PALETTE.amber, PALETTE.rose, PALETTE.blue, PALETTE.teal];

async function api(path) {
  const r = await fetch(path, { headers: { "X-Api-Key": API_KEY } });
  if (!r.ok) throw new Error(`${path} -> HTTP ${r.status}`);
  return r.json();
}
async function apiPost(path, body) {
  const r = await fetch(path, {
    method: "POST",
    headers: { "X-Api-Key": API_KEY, "Content-Type": "application/json" },
    body: JSON.stringify(body)
  });
  return r.json();
}

/* ---------- formatting ---------- */
const fmtNum = n => n == null ? "–" : Number(n).toLocaleString("en-MY");
const fmtCompact = n => n == null ? "–" : Intl.NumberFormat("en", { notation: "compact", maximumFractionDigits: 1 }).format(n);
const fmtRM = n => n == null ? "–" : "RM " + fmtCompact(n);
const fmtRMFull = n => n == null ? "–" : "RM " + Number(n).toLocaleString("en-MY", { maximumFractionDigits: 0 });
const fmtRM2 = n => n == null ? "–" : "RM " + Number(n).toFixed(2);
const fmtPct = n => n == null ? "–" : Number(n).toFixed(1) + "%";
const fmtDate = d => new Date(d).toLocaleDateString("en-MY", { day: "numeric", month: "short" });
const fmtDateFull = d => new Date(d).toLocaleDateString("en-MY", { day: "numeric", month: "short", year: "numeric" });
const fmtTime = d => d ? new Date(d).toLocaleTimeString("en-MY", { hour: "2-digit", minute: "2-digit", second: "2-digit" }) : "–";
const hourLabel = h => (h % 12 || 12) + (h < 12 ? "am" : "pm");

/* ---------- shell ---------- */
const NAV = [
  { href: "index.html",     key: "overview",  label: "Overview",         icon: '<rect x="3" y="3" width="7" height="9" rx="1.5"/><rect x="14" y="3" width="7" height="5" rx="1.5"/><rect x="14" y="12" width="7" height="9" rx="1.5"/><rect x="3" y="16" width="7" height="5" rx="1.5"/>' },
  { href: "realtime.html",  key: "realtime",  label: "Real-Time Status", icon: '<circle cx="12" cy="12" r="9"/><path d="M12 7v5l3 3"/>' },
  { href: "occupancy.html", key: "occupancy", label: "Occupancy",        icon: '<path d="M3 21V8l9-5 9 5v13"/><path d="M9 21v-6h6v6"/>' },
  { href: "vehicle.html",   key: "vehicle",   label: "Vehicle Analysis", icon: '<path d="M5 16l1.5-5.5A2 2 0 0 1 8.4 9h7.2a2 2 0 0 1 1.9 1.5L19 16"/><rect x="4" y="15" width="16" height="5" rx="1.5"/><circle cx="8" cy="20" r="1"/><circle cx="16" cy="20" r="1"/>' },
  { href: "behaviour.html", key: "behaviour", label: "Driver Behaviour", icon: '<circle cx="9" cy="7" r="3"/><path d="M3 21v-1a6 6 0 0 1 6-6"/><circle cx="17" cy="9" r="2.5"/><path d="M13 21v-1a4 4 0 0 1 8 0v1"/>' },
  { href: "events.html",    key: "events",    label: "Events Impact",    icon: '<rect x="3" y="5" width="18" height="16" rx="2"/><path d="M3 10h18M8 3v4M16 3v4"/>' },
  { href: "revenue.html",   key: "revenue",   label: "Revenue",          icon: '<circle cx="12" cy="12" r="9"/><path d="M9.5 8.5h5M9.5 11.5h5M12 8.5v8"/>' },
  { href: "forecast.html",  key: "forecast",  label: "Forecast (ML)",    icon: '<path d="M3 17l5-6 4 3 6-8"/><path d="M18 6h3v3"/><path d="M3 21h18"/>' },
  { href: "gate.html",      key: "gate",      label: "Gate Monitor",     icon: '<path d="M4 21V5a2 2 0 0 1 2-2h2v18"/><path d="M8 8h13l-2.5 4H8"/><path d="M4 21h6"/>' },
  { href: "connect.html",   key: "connect",   label: "Data Source",      icon: '<ellipse cx="12" cy="5" rx="8" ry="3"/><path d="M4 5v6c0 1.7 3.6 3 8 3s8-1.3 8-3V5"/><path d="M4 11v6c0 1.7 3.6 3 8 3s8-1.3 8-3v-6"/>' }
];

function buildShell() {
  const body = document.body;
  const page = body.dataset.page;
  const hasYear = body.dataset.year === "1";

  const navHtml = NAV.map(n =>
    `<a href="${n.href}" class="${n.key === page ? "active" : ""}"${n.key === page ? ' aria-current="page"' : ""}>
       <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">${n.icon}</svg>
       ${n.label}</a>`).join("");

  body.insertAdjacentHTML("afterbegin", `
    <aside class="sidebar">
      <div class="brand">
        <div class="brand-logo" aria-hidden="true">P</div>
        <div><div class="brand-name">Confined Parking</div><div class="brand-sub">Analytics System</div></div>
      </div>
      <nav class="nav"><div class="nav-section">Dashboards</div>${navHtml}</nav>
      <div class="sidebar-foot">
        <div class="api-status"><span class="dot" id="apiDot"></span><span id="apiStatusText">Checking API…</span></div>
        <div style="margin-top:6px">11,000 bays · PostgreSQL 16</div>
      </div>
    </aside>`);

  const main = document.querySelector(".main");
  main.insertAdjacentHTML("afterbegin", `
    <div class="topbar">
      <div>
        <h1 class="page-title">${body.dataset.title || ""}</h1>
        <p class="page-sub">${body.dataset.sub || ""}</p>
      </div>
      <div class="topbar-right">
        ${hasYear ? `<div class="filter-stack">
          <select class="month-select" id="rangePreset" aria-label="Date range preset">
            ${RANGE_PRESETS.map(([v, l]) => `<option value="${v}">${l}</option>`).join("")}
          </select>
          <input type="date" class="month-select date-input" id="dateFrom" aria-label="From date">
          <span style="color:var(--text-faint)">–</span>
          <input type="date" class="month-select date-input" id="dateTo" aria-label="To date">
        </div>` : ""}
        <div class="clock" id="clock"></div>
      </div>
    </div>`);

  // clock
  const tick = () => document.getElementById("clock").textContent =
    new Date().toLocaleString("en-MY", { weekday: "short", day: "numeric", month: "short", hour: "2-digit", minute: "2-digit", second: "2-digit" });
  tick(); setInterval(tick, 1000);

  // API health check
  api("/api/occupancy")
    .then(() => { document.getElementById("apiDot").className = "dot ok"; document.getElementById("apiStatusText").textContent = "API connected"; })
    .catch(() => { document.getElementById("apiDot").className = "dot err"; document.getElementById("apiStatusText").textContent = "API unreachable"; });

  // date-range filter
  if (hasYear) {
    const sel = document.getElementById("rangePreset");
    const fIn = document.getElementById("dateFrom");
    const tIn = document.getElementById("dateTo");

    const sync = () => {
      const [f, t] = dashRange();
      sel.value = localStorage.getItem("dashPreset") || DEFAULT_PRESET;
      fIn.value = f; tIn.value = t;
    };
    sync();

    const rerender = () => { window.initPage && window.initPage(); };

    sel.addEventListener("change", () => {
      localStorage.setItem("dashPreset", sel.value);
      sync(); rerender();
    });
    const onDate = () => {
      if (!fIn.value || !tIn.value) return;
      localStorage.setItem("dashPreset", "custom");
      localStorage.setItem("dashFrom", fIn.value);
      localStorage.setItem("dashTo", tIn.value);
      sel.value = "custom";
      rerender();
    };
    fIn.addEventListener("change", onDate);
    tIn.addEventListener("change", onDate);
  }
  return null;
}

/* ---------- date-range helpers ---------- */
const RANGE_PRESETS = [
  ["all", "All data"], ["2025", "Year 2025"], ["2026", "Year 2026"],
  ["30d", "Last 30 days"], ["90d", "Last 90 days"], ["custom", "Custom range"]
];
const DEFAULT_PRESET = "2026";
const isoDate = d => `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, "0")}-${String(d.getDate()).padStart(2, "0")}`;

function dashRange() {
  const p = localStorage.getItem("dashPreset") || DEFAULT_PRESET;
  const today = new Date();
  const ago = n => { const d = new Date(); d.setDate(d.getDate() - n); return d; };
  switch (p) {
    case "2025": return ["2025-01-01", "2025-12-31"];
    case "2026": return ["2026-01-01", "2026-12-31"];
    case "30d": return [isoDate(ago(30)), isoDate(today)];
    case "90d": return [isoDate(ago(90)), isoDate(today)];
    case "custom": return [
      localStorage.getItem("dashFrom") || "2025-01-01",
      localStorage.getItem("dashTo") || isoDate(today)];
    default: return ["2025-01-01", isoDate(today)];   // all data
  }
}
function dashQuery() {
  const [f, t] = dashRange();
  return `from=${f}&to=${t}`;
}
function filterLabel() {
  const p = localStorage.getItem("dashPreset") || DEFAULT_PRESET;
  const preset = RANGE_PRESETS.find(([v]) => v === p);
  if (p !== "custom" && preset) return preset[1];
  const [f, t] = dashRange();
  return `${fmtDate(f)} – ${fmtDate(t)}`;
}

function startAutoRefresh(refreshFn, seconds = 60) {
  return setInterval(() => { if (!document.hidden) refreshFn(); }, seconds * 1000);
}

/* ---------- Chart.js defaults & factory ---------- */
function chartDefaults() {
  Chart.defaults.color = "#8b9bb8";
  Chart.defaults.borderColor = "rgba(255,255,255,.055)";
  Chart.defaults.font.family = '"Segoe UI", system-ui, sans-serif';
  Chart.defaults.font.size = 11.5;
  Chart.defaults.plugins.legend.labels.boxWidth = 12;
  Chart.defaults.plugins.legend.labels.boxHeight = 12;
  Chart.defaults.plugins.legend.labels.usePointStyle = true;
  Chart.defaults.plugins.legend.labels.pointStyle = "circle";
  Chart.defaults.plugins.tooltip.backgroundColor = "#182238";
  Chart.defaults.plugins.tooltip.borderColor = "#2a3a60";
  Chart.defaults.plugins.tooltip.borderWidth = 1;
  Chart.defaults.plugins.tooltip.padding = 10;
  Chart.defaults.plugins.tooltip.cornerRadius = 8;
  Chart.defaults.animation = false;
}

const charts = {};
function makeChart(id, config) {
  if (charts[id]) charts[id].destroy();
  config.options = config.options || {};
  config.options.animation = false;
  config.options.maintainAspectRatio = false;
  charts[id] = new Chart(document.getElementById(id), config);
  return charts[id];
}

/* simple donut factory */
function donut(id, labels, values, { cutout = "62%", colors = SERIES, fmt = fmtNum } = {}) {
  return makeChart(id, {
    type: "doughnut",
    data: { labels, datasets: [{ data: values, backgroundColor: colors.slice(0, labels.length).map(c => hexA(c, .8)), borderColor: "#0b1120", borderWidth: 2 }] },
    options: {
      cutout,
      plugins: {
        legend: { position: "right" },
        tooltip: { callbacks: { label: c => {
          const total = c.dataset.data.reduce((a, b) => a + Number(b), 0);
          return ` ${c.label}: ${fmt(c.parsed)} (${(100 * c.parsed / total).toFixed(1)}%)`;
        } } }
      }
    }
  });
}

function areaGradient(ctx, hex, alphaTop = 0.28) { return hexA(hex, alphaTop); }
const hexA = (hex, a) => {
  const n = parseInt(hex.slice(1), 16);
  return `rgba(${(n >> 16) & 255},${(n >> 8) & 255},${n & 255},${a})`;
};

/* constant dashed threshold line dataset (e.g. 85% pressure line) */
function thresholdLine(n, value, color) {
  return {
    label: "__threshold", data: Array(n).fill(value),
    borderColor: hexA(color, .6), borderDash: [6, 6], borderWidth: 1.5,
    pointRadius: 0, fill: false, tension: 0
  };
}
const hideThreshold = { legend: { labels: { filter: i => i.text !== "__threshold" } } };

/* ---------- heatmap color scale (0-100% occupancy) ---------- */
function heatColor(v) {
  if (v == null) return "#141d33";
  const stops = [
    [0,   [20, 29, 51]],
    [35,  [23, 78, 134]],
    [60,  [34, 148, 189]],
    [80,  [251, 191, 36]],
    [100, [244, 63, 94]]
  ];
  const x = Math.max(0, Math.min(100, v));
  for (let i = 1; i < stops.length; i++) {
    if (x <= stops[i][0]) {
      const [a, ca] = stops[i - 1], [b, cb] = stops[i];
      const t = (x - a) / (b - a);
      const c = ca.map((cA, j) => Math.round(cA + t * (cb[j] - cA)));
      return `rgb(${c[0]},${c[1]},${c[2]})`;
    }
  }
  return "rgb(244,63,94)";
}

/* ---------- KPI + table + badge helpers ---------- */
function kpi(el, { label, value, sub = "", color = PALETTE.accent }) {
  el.insertAdjacentHTML("beforeend", `
    <div class="kpi" style="--kpi-color:${color}">
      <div class="kpi-label">${label}</div>
      <div class="kpi-value">${value}</div>
      <div class="kpi-sub">${sub}</div>
    </div>`);
}

const badge = (text, type = "grey") => `<span class="badge ${type}">${text}</span>`;
const dayTypeBadge = t => badge(t,
  t === "Event" ? "violet" : t === "Public Holiday" ? "amber" : t === "Weekend" ? "blue" : "grey");

/* columns: [{ h: "Header", c: row => cellHtml, num: true }] */
function buildTable(elId, columns, rows, emptyMsg = "No data") {
  const el = document.getElementById(elId);
  if (!rows || rows.length === 0) {
    el.innerHTML = `<div style="color:var(--text-faint);padding:20px;text-align:center">${emptyMsg}</div>`;
    return;
  }
  el.innerHTML = `<table class="tbl">
    <thead><tr>${columns.map(c => `<th class="${c.num ? "num" : ""}">${c.h}</th>`).join("")}</tr></thead>
    <tbody>${rows.map(r => `<tr>${columns.map(c => `<td class="${c.num ? "num" : ""}">${c.c(r)}</td>`).join("")}</tr>`).join("")}</tbody>
  </table>`;
}

function showError(err) {
  console.error(err);
  const main = document.querySelector(".main");
  if (!document.getElementById("loadErr"))
    main.insertAdjacentHTML("beforeend",
      `<div class="banner" id="loadErr" style="border-color:rgba(251,113,133,.4);background:rgba(251,113,133,.07);color:#fda4af">
         Could not load data — is the API running (<code>dotnet run</code>) and PostgreSQL up? <span style="opacity:.7">${err.message || err}</span>
       </div>`);
}
/* end of shared helpers */
