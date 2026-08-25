export const CARD_COLLAPSE_STORAGE_KEY = "nexus-card-collapse-v1";
export const CARD_COLLAPSED_CLASS = "is-collapsed";
export const CARD_COLLAPSIBLE_CLASS = "is-collapsible";

const INTERACTIVE_SELECTOR =
  "button, a, input, select, textarea, label, option, [role='button'], [data-collapse-ignore]";

export function normalizeCollapseTitle(text) {
  return String(text || "").replace(/\s+/g, " ").trim();
}

export function makeCollapseKey({ viewId = "root", collapseId, strongId, title } = {}) {
  const view = String(viewId || "root");
  if (collapseId) return `${view}/${collapseId}`;
  if (strongId) return `${view}/#${strongId}`;
  return `${view}/${normalizeCollapseTitle(title) || "card"}`;
}

export function uniquifyCollapseKeys(keys) {
  const seen = new Map();
  return (keys || []).map((key) => {
    const n = (seen.get(key) || 0) + 1;
    seen.set(key, n);
    return n === 1 ? key : `${key}#${n}`;
  });
}

export function parseCollapsedMap(raw) {
  if (!raw) return {};
  try {
    const parsed = JSON.parse(raw);
    if (Array.isArray(parsed)) {
      return Object.fromEntries(parsed.filter((key) => typeof key === "string").map((key) => [key, true]));
    }
    if (!parsed || typeof parsed !== "object") return {};
    const out = {};
    for (const [key, value] of Object.entries(parsed)) {
      if (value === true) out[key] = true;
    }
    return out;
  } catch {
    return {};
  }
}

export function serializeCollapsedMap(map) {
  const keys = Object.keys(map || {}).filter((key) => map[key] === true).sort();
  return JSON.stringify(Object.fromEntries(keys.map((key) => [key, true])));
}

export function isInteractiveTarget(target) {
  if (!target || typeof target.closest !== "function") return false;
  return Boolean(target.closest(INTERACTIVE_SELECTOR));
}

export function shouldToggleCollapse(target) {
  if (!target || typeof target.closest !== "function") return false;
  if (!target.closest(".card-header")) return false;
  if (target.closest(".card-collapse-btn")) return true;
  return !isInteractiveTarget(target);
}

export function cardCollapseKey(card) {
  const header = card.querySelector(":scope > .card-header");
  const strong = header?.querySelector("strong");
  const view = card.closest(".workspace");
  return makeCollapseKey({
    viewId: view?.id || "root",
    collapseId: card.dataset.collapseId,
    strongId: strong?.id,
    title: strong?.textContent,
  });
}

function readMap(storage) {
  try {
    return parseCollapsedMap(storage?.getItem?.(CARD_COLLAPSE_STORAGE_KEY));
  } catch {
    return {};
  }
}

function writeMap(storage, map) {
  try {
    storage?.setItem?.(CARD_COLLAPSE_STORAGE_KEY, serializeCollapsedMap(map));
  } catch {
    /* localStorage 不可用时忽略 */
  }
}

export function listCollapsibleCards(root) {
  if (!root?.querySelectorAll) return [];
  return [...root.querySelectorAll(".card")].filter((card) => {
    if (card.dataset?.noCollapse === "true") return false;
    const header = card.querySelector(":scope > .card-header");
    if (!header) return false;
    return [...card.children].some((child) => child !== header);
  });
}

export function setCardCollapsed(card, collapsed, key, storage) {
  card.classList.toggle(CARD_COLLAPSED_CLASS, Boolean(collapsed));
  const header = card.querySelector(":scope > .card-header");
  const btn = header?.querySelector(".card-collapse-btn");
  const title = normalizeCollapseTitle(header?.querySelector("strong")?.textContent) || "卡片";
  if (btn) {
    btn.setAttribute("aria-expanded", collapsed ? "false" : "true");
    btn.setAttribute("aria-label", collapsed ? `展开 ${title}` : `折叠 ${title}`);
  }
  if (key && storage) {
    const map = readMap(storage);
    if (collapsed) map[key] = true;
    else delete map[key];
    writeMap(storage, map);
  }
}

function ensureCollapseButton(header, title) {
  let btn = header.querySelector(":scope > .card-collapse-btn");
  if (!btn) {
    btn = header.ownerDocument.createElement("button");
    btn.type = "button";
    btn.classList.add("card-collapse-btn");
    const mark = header.ownerDocument.createElement("span");
    mark.classList.add("card-collapse-mark");
    mark.setAttribute("aria-hidden", "true");
    btn.appendChild(mark);
    header.insertBefore(btn, header.firstChild);
  }
  const label = normalizeCollapseTitle(title) || "卡片";
  btn.title = "折叠 / 展开";
  btn.setAttribute("aria-label", `折叠 ${label}`);
  btn.setAttribute("aria-expanded", "true");
  return btn;
}

export function initCardCollapse(root = globalThis.document, storage = globalThis.localStorage) {
  if (!root?.querySelectorAll) return 0;
  const host = root.documentElement || root;
  if (host.dataset?.cardCollapseReady === "true") return 0;
  if (host.dataset) host.dataset.cardCollapseReady = "true";

  const cards = listCollapsibleCards(root);
  const keys = uniquifyCollapseKeys(cards.map((card) => cardCollapseKey(card)));
  const saved = readMap(storage);

  cards.forEach((card, index) => {
    const key = keys[index];
    const header = card.querySelector(":scope > .card-header");
    if (!header) return;
    const title = header.querySelector("strong")?.textContent;
    card.classList.add(CARD_COLLAPSIBLE_CLASS);
    card.dataset.collapseKey = key;
    ensureCollapseButton(header, title);
    setCardCollapsed(card, saved[key] === true);
    header.addEventListener("click", (event) => {
      if (!shouldToggleCollapse(event.target)) return;
      setCardCollapsed(card, !card.classList.contains(CARD_COLLAPSED_CLASS), key, storage);
    });
  });

  return cards.length;
}
