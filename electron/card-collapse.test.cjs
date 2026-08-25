"use strict";

const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const { pathToFileURL } = require("node:url");

const root = path.resolve(__dirname, "..");
const moduleUrl = pathToFileURL(path.join(root, "src", "card-collapse.js")).href;

function memoryStorage(initial = {}) {
  const data = { ...initial };
  return {
    getItem(key) {
      return Object.prototype.hasOwnProperty.call(data, key) ? data[key] : null;
    },
    setItem(key, value) {
      data[key] = String(value);
    },
    data,
  };
}

function fakeEl(tag, attrs = {}) {
  const classNames = new Set(String(attrs.class || "").split(/\s+/).filter(Boolean));
  const dataset = { ...(attrs.dataset || {}) };
  const attributes = {};
  const listeners = {};
  const node = {
    tagName: String(tag).toUpperCase(),
    children: [],
    parentNode: null,
    textContent: attrs.text || "",
    id: attrs.id || "",
    ownerDocument: null,
    dataset,
    classList: {
      add(name) { classNames.add(name); },
      remove(name) { classNames.delete(name); },
      contains(name) { return classNames.has(name); },
      toggle(name, force) {
        const on = force === undefined ? !classNames.has(name) : Boolean(force);
        if (on) classNames.add(name);
        else classNames.delete(name);
        return on;
      },
    },
    appendChild(child) {
      child.parentNode = node;
      node.children.push(child);
      return child;
    },
    insertBefore(child, before) {
      child.parentNode = node;
      const index = node.children.indexOf(before);
      if (index < 0) node.children.push(child);
      else node.children.splice(index, 0, child);
      return child;
    },
    matches(selector) {
      return String(selector).split(",").some((part) => {
        const sel = part.trim();
        if (sel.startsWith(".")) return classNames.has(sel.slice(1));
        if (sel === "button") return node.tagName === "BUTTON";
        if (sel === "strong") return node.tagName === "STRONG";
        if (sel === "a") return node.tagName === "A";
        if (sel === "input") return node.tagName === "INPUT";
        if (sel === "select") return node.tagName === "SELECT";
        if (sel === "textarea") return node.tagName === "TEXTAREA";
        if (sel === "label") return node.tagName === "LABEL";
        if (sel === "option") return node.tagName === "OPTION";
        if (sel === "[role='button']") return attributes.role === "button";
        if (sel === "[data-collapse-ignore]") return Object.prototype.hasOwnProperty.call(dataset, "collapseIgnore");
        return false;
      });
    },
    closest(selector) {
      let current = node;
      while (current) {
        if (current.matches?.(selector)) return current;
        current = current.parentNode;
      }
      return null;
    },
    querySelector(selector) {
      return node.querySelectorAll(selector)[0] || null;
    },
    querySelectorAll(selector) {
      const out = [];
      const visit = (el) => {
        if (selector === ":scope > .card-header" && el.parentNode === node && el.classList.contains("card-header")) out.push(el);
        else if (selector === ":scope > .card-collapse-btn" && el.parentNode === node && el.classList.contains("card-collapse-btn")) out.push(el);
        else if (selector === ".card-collapse-btn" && el.classList.contains("card-collapse-btn")) out.push(el);
        else if (selector === "strong" && el.tagName === "STRONG") out.push(el);
        else if (selector === ".card" && el.classList.contains("card")) out.push(el);
        else if (selector === ".workspace" && el.classList.contains("workspace")) out.push(el);
        for (const child of el.children) visit(child);
      };
      for (const child of node.children) visit(child);
      if (selector === ".card" && node.classList.contains("card")) out.unshift(node);
      return out;
    },
    setAttribute(name, value) {
      attributes[name] = String(value);
    },
    getAttribute(name) {
      return attributes[name] ?? null;
    },
    addEventListener(type, fn) {
      (listeners[type] ||= []).push(fn);
    },
    dispatch(type, target = node) {
      for (const fn of listeners[type] || []) fn({ target, type });
    },
    _attributes: attributes,
    get firstChild() {
      return node.children[0] || null;
    },
  };
  if (attrs.id) node.id = attrs.id;
  return node;
}

function makeDoc() {
  const document = {
    createElement(tag) {
      const el = fakeEl(tag);
      el.ownerDocument = document;
      return el;
    },
  };
  const root = fakeEl("div");
  root.ownerDocument = document;
  root.dataset = {};
  root.documentElement = root;
  document.documentElement = root;
  const originalQuery = root.querySelectorAll.bind(root);
  root.querySelectorAll = (selector) => {
    if (selector === ".card") {
      const cards = [];
      const walk = (el) => {
        if (el.classList.contains("card")) cards.push(el);
        for (const child of el.children) walk(child);
      };
      for (const child of root.children) walk(child);
      return cards;
    }
    return originalQuery(selector);
  };
  return { document, root };
}

function makeCard(root, { viewId, title, strongId, collapseId, noCollapse, extraButton } = {}) {
  const view = fakeEl("section", { class: "workspace", id: viewId || "interfaces-view" });
  view.id = viewId || "interfaces-view";
  const card = fakeEl("div", { class: "card" });
  if (collapseId) card.dataset.collapseId = collapseId;
  if (noCollapse) card.dataset.noCollapse = "true";
  const header = fakeEl("div", { class: "card-header" });
  const strong = fakeEl("strong", { id: strongId, text: title || "网卡" });
  strong.id = strongId || "";
  strong.textContent = title || "网卡";
  header.appendChild(strong);
  let extra = null;
  if (extraButton) {
    extra = fakeEl("button", { class: "btn-ghost", text: "刷新" });
    header.appendChild(extra);
  }
  const body = fakeEl("div", { class: "card-body", text: "body" });
  card.appendChild(header);
  card.appendChild(body);
  view.appendChild(card);
  root.appendChild(view);
  for (const el of [view, card, header, strong, extra, body].filter(Boolean)) {
    el.ownerDocument = root.ownerDocument;
  }
  return { view, card, header, strong, body, extra };
}

test("collapse keys stay stable for title changes when the heading has an id", async () => {
  const {
    makeCollapseKey,
    uniquifyCollapseKeys,
    normalizeCollapseTitle,
  } = await import(moduleUrl);

  assert.equal(normalizeCollapseTitle("  COM 口 / USB  "), "COM 口 / USB");
  assert.equal(makeCollapseKey({ viewId: "workspace", strongId: "connection-title", title: "串口配置" }), "workspace/#connection-title");
  assert.equal(makeCollapseKey({ viewId: "workspace", strongId: "connection-title", title: "TCP 配置" }), "workspace/#connection-title");
  assert.equal(makeCollapseKey({ viewId: "interfaces-view", title: "网卡" }), "interfaces-view/网卡");
  assert.equal(makeCollapseKey({ viewId: "melsec-view", collapseId: "rw" }), "melsec-view/rw");
  assert.deepEqual(
    uniquifyCollapseKeys(["a/离线报文解析", "a/离线报文解析", "a/连接"]),
    ["a/离线报文解析", "a/离线报文解析#2", "a/连接"],
  );
});

test("collapsed storage only keeps true keys and ignores junk", async () => {
  const { parseCollapsedMap, serializeCollapsedMap } = await import(moduleUrl);
  assert.deepEqual(parseCollapsedMap("{"), {});
  assert.deepEqual(parseCollapsedMap(JSON.stringify({ "interfaces-view/网卡": true, skip: false, n: 1 })), {
    "interfaces-view/网卡": true,
  });
  assert.deepEqual(parseCollapsedMap(JSON.stringify(["interfaces-view/COM 口 / USB 转串口适配器"])), {
    "interfaces-view/COM 口 / USB 转串口适配器": true,
  });
  assert.equal(
    serializeCollapsedMap({ b: true, a: true, c: false }),
    JSON.stringify({ a: true, b: true }),
  );
});

test("header clicks ignore action buttons but accept the chevron and title", async () => {
  const { shouldToggleCollapse, isInteractiveTarget } = await import(moduleUrl);
  const header = fakeEl("div", { class: "card-header" });
  const title = fakeEl("strong", { text: "网卡" });
  const refresh = fakeEl("button", { class: "btn-ghost", text: "一键刷新" });
  const chevron = fakeEl("button", { class: "card-collapse-btn" });
  header.appendChild(chevron);
  header.appendChild(title);
  header.appendChild(refresh);

  assert.equal(shouldToggleCollapse(title), true);
  assert.equal(shouldToggleCollapse(header), true);
  assert.equal(shouldToggleCollapse(chevron), true);
  assert.equal(shouldToggleCollapse(refresh), false);
  assert.equal(isInteractiveTarget(refresh), true);
  assert.equal(shouldToggleCollapse(fakeEl("div", { class: "card-body" })), false);
});

test("init collapses remembered cards and does not toggle when clicking refresh", async () => {
  const { initCardCollapse, CARD_COLLAPSE_STORAGE_KEY, CARD_COLLAPSED_CLASS } = await import(moduleUrl);
  const { root } = makeDoc();
  const first = makeCard(root, { title: "网卡" });
  const second = makeCard(root, { title: "USB 设备(全部)", extraButton: true });
  const skipped = makeCard(root, { title: "固定", noCollapse: true });
  const storage = memoryStorage({
    [CARD_COLLAPSE_STORAGE_KEY]: JSON.stringify({ "interfaces-view/网卡": true }),
  });

  assert.equal(initCardCollapse(root, storage), 2);
  assert.equal(initCardCollapse(root, storage), 0);
  assert.equal(first.card.classList.contains(CARD_COLLAPSED_CLASS), true);
  assert.equal(second.card.classList.contains(CARD_COLLAPSED_CLASS), false);
  assert.equal(skipped.card.classList.contains("is-collapsible"), false);
  assert.equal(first.header.children[0].classList.contains("card-collapse-btn"), true);

  const refresh = second.extra;
  second.header.dispatch("click", refresh);
  assert.equal(second.card.classList.contains(CARD_COLLAPSED_CLASS), false);

  second.header.dispatch("click", second.strong);
  assert.equal(second.card.classList.contains(CARD_COLLAPSED_CLASS), true);
  const saved = JSON.parse(storage.getItem(CARD_COLLAPSE_STORAGE_KEY));
  assert.equal(saved["interfaces-view/USB 设备(全部)"], true);
});

test("workspace cards and main bootstrap are wired for collapse", () => {
  const html = fs.readFileSync(path.join(root, "index.html"), "utf8");
  const main = fs.readFileSync(path.join(root, "src", "main.js"), "utf8");
  const css = fs.readFileSync(path.join(root, "src", "app.css"), "utf8");
  const cards = html.match(/<div class="card\b[^"]*">/g) || [];
  assert.ok(cards.length >= 50, `expected many cards, got ${cards.length}`);
  assert.match(html, /<strong>网卡<\/strong>/);
  assert.match(html, /<strong>COM 口 \/ USB 转串口适配器<\/strong>/);
  assert.match(html, /<strong>USB 设备\(全部\)<\/strong>/);
  assert.match(main, /initCardCollapse\(document\)/);
  assert.match(css, /\.card\.is-collapsed/);
  assert.match(css, /\.card-collapse-btn/);
});
