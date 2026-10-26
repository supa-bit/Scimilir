// Scimilir's chat page. It talks to the Scimilir program on the PC through
// the /api addresses (see Api.cs).
"use strict";

const $ = (id) => document.getElementById(id);
const touchScreen = matchMedia("(pointer: coarse)").matches;

const state = {
  chats: [],          // [{id, title, updated}]
  chat: null,         // the open chat: {id, title, model, messages: [...]}, or null for a new one
  models: [],         // [{id, name, group, detail}]
  model: null,        // id of the picked model
  pending: null,      // the reply being written: {content, thinking, status, error, model}
  controller: null,   // lets Stop cancel the reply
};

const STARTERS = [
  { label: "Qwen3 4B (small, smart)", repo: "bartowski/Qwen_Qwen3-4B-GGUF" },
  { label: "Llama 3.2 3B (small, fast)", repo: "bartowski/Llama-3.2-3B-Instruct-GGUF" },
  { label: "Gemma 3 4B", repo: "bartowski/google_gemma-3-4b-it-GGUF" },
  { label: "Qwen3 8B (needs ~6 GB)", repo: "bartowski/Qwen_Qwen3-8B-GGUF" },
];

// ---------- Talking to the server ----------

class LoggedOut extends Error {}

async function api(path, options = {}) {
  const init = { method: options.method || "GET", headers: {} };
  if (options.body !== undefined) {
    init.headers["Content-Type"] = "application/json";
    init.body = JSON.stringify(options.body);
  }
  const res = await fetch("/api" + path, init);
  if (res.status === 401 && !options.allow401) {
    showGate("login");
    throw new LoggedOut("Logged out");
  }
  const text = await res.text();
  const data = text ? JSON.parse(text) : null;
  if (!res.ok) throw new Error((data && data.message) || `Something went wrong (${res.status}).`);
  return data;
}

function toast(message, ms = 4000) {
  const t = $("toast");
  t.textContent = message;
  t.hidden = false;
  clearTimeout(toast.timer);
  toast.timer = setTimeout(() => (t.hidden = true), ms);
}

function report(err) {
  if (!(err instanceof LoggedOut)) toast(err.message || String(err));
}

function el(tag, props = {}, ...children) {
  const node = document.createElement(tag);
  for (const [k, v] of Object.entries(props)) {
    if (k === "class") node.className = v;
    else if (k === "html") node.innerHTML = v;
    else if (k.startsWith("on")) node.addEventListener(k.slice(2), v);
    else if (v !== false && v != null) node.setAttribute(k, v === true ? "" : v);
  }
  for (const c of children.flat()) if (c != null) node.append(c);
  return node;
}

const icons = {
  copy: '<svg viewBox="0 0 24 24"><rect x="9" y="9" width="11" height="11" rx="2"/><path d="M5 15V5a2 2 0 0 1 2-2h8"/></svg>',
  retry: '<svg viewBox="0 0 24 24"><path d="M3 12a9 9 0 0 1 15.5-6.3L21 8M21 3v5h-5M21 12a9 9 0 0 1-15.5 6.3L3 16M3 21v-5h5"/></svg>',
  trash: '<svg viewBox="0 0 24 24"><path d="M4 7h16M10 11v6M14 11v6M6 7l1 13h10l1-13M9 7V4h6v3"/></svg>',
  pencil: '<svg viewBox="0 0 24 24"><path d="M4 20h4L19 9l-4-4L4 16v4z"/></svg>',
};

async function copyText(text) {
  try {
    await navigator.clipboard.writeText(text);
  } catch {
    // Phones block the clipboard on plain http:// pages, so use the old way.
    const area = el("textarea", { style: "position:fixed;opacity:0" });
    area.value = text;
    document.body.append(area);
    area.select();
    document.execCommand("copy");
    area.remove();
  }
  toast("Copied", 1500);
}

// ---------- Login ----------

let gateMode = "login";

function showGate(mode) {
  gateMode = mode;
  $("app").hidden = true;
  $("gate").hidden = false;
  $("gate-error").textContent = "";
  const pw = $("gate-password");
  const pw2 = $("gate-password2");
  const button = $("gate-button");
  pw.value = pw2.value = "";
  pw.hidden = false;
  button.hidden = false;
  pw2.hidden = mode !== "setup";
  pw2.required = mode === "setup";
  if (mode === "setup") {
    $("gate-text").textContent = "Welcome! Choose a password. You'll use it to log in from your phone and other devices.";
    pw.autocomplete = "new-password";
    button.textContent = "Start";
  } else if (mode === "setup-elsewhere") {
    $("gate-text").textContent = "Scimilir isn't set up yet. Open it on the PC it runs on to choose a password, then come back here.";
    pw.hidden = true;
    button.hidden = true;
  } else {
    $("gate-text").textContent = "Enter your password.";
    pw.autocomplete = "current-password";
    button.textContent = "Log in";
  }
  if (!pw.hidden) pw.focus();
}

$("gate-form").addEventListener("submit", async (e) => {
  e.preventDefault();
  const password = $("gate-password").value;
  $("gate-error").textContent = "";
  if (gateMode === "setup" && password !== $("gate-password2").value) {
    $("gate-error").textContent = "The two passwords don't match.";
    return;
  }
  try {
    await api(gateMode === "setup" ? "/setup" : "/login", { method: "POST", body: { password }, allow401: true });
    startApp();
  } catch (err) {
    $("gate-error").textContent = err.message;
  }
});

async function boot() {
  try {
    const status = await api("/status");
    if (status.setupNeeded) showGate(status.fromThisPc ? "setup" : "setup-elsewhere");
    else if (!status.loggedIn) showGate("login");
    else startApp();
  } catch (err) {
    $("gate").hidden = false;
    $("gate-text").textContent = "Can't reach Scimilir. Is it still running on the PC?";
    $("gate-password").hidden = $("gate-button").hidden = true;
  }
}

async function startApp() {
  $("gate").hidden = true;
  $("app").hidden = false;
  await Promise.all([loadModels(), loadChats()]).catch(report);
  await route();
}

// ---------- Chats ----------

async function loadChats() {
  state.chats = await api("/chats");
  renderChatList();
}

function dayGroup(iso) {
  const day = 86400000;
  const today = new Date();
  today.setHours(0, 0, 0, 0);
  const t = new Date(iso).getTime();
  if (t >= today.getTime()) return "Today";
  if (t >= today.getTime() - day) return "Yesterday";
  if (t >= today.getTime() - 7 * day) return "Previous 7 days";
  if (t >= today.getTime() - 30 * day) return "Previous 30 days";
  return "Older";
}

function renderChatList() {
  const list = $("chat-list");
  list.replaceChildren();
  if (!state.chats.length) {
    list.append(el("div", { class: "empty-list" }, "Your chats will appear here."));
    return;
  }
  let group = null;
  for (const c of state.chats) {
    const g = dayGroup(c.updated);
    if (g !== group) {
      group = g;
      list.append(el("div", { class: "chat-group" }, g));
    }
    list.append(el("div", { class: "chat-item" + (state.chat?.id === c.id ? " active" : "") },
      el("a", { href: `#/chat/${c.id}`, title: c.title, onclick: closeSidebar }, c.title),
      el("button", { class: "icon-button", title: "Rename", "aria-label": "Rename chat", html: icons.pencil, onclick: () => renameChat(c) }),
      el("button", { class: "icon-button", title: "Delete", "aria-label": "Delete chat", html: icons.trash, onclick: () => deleteChat(c) })));
  }
}

async function renameChat(c) {
  const title = prompt("New name for this chat:", c.title);
  if (!title || !title.trim()) return;
  try {
    await api(`/chats/${c.id}`, { method: "PATCH", body: { title } });
    if (state.chat?.id === c.id) state.chat.title = title;
    await loadChats();
  } catch (err) { report(err); }
}

async function deleteChat(c) {
  if (!confirm(`Delete "${c.title}"? This can't be undone.`)) return;
  try {
    await api(`/chats/${c.id}`, { method: "DELETE" });
    if (state.chat?.id === c.id) location.hash = "#/";
    await loadChats();
  } catch (err) { report(err); }
}

async function route() {
  const m = location.hash.match(/^#\/chat\/([a-z0-9]+)$/);
  if (state.controller) {
    // Stay on the chat whose reply is still streaming in.
    if (m?.[1] !== state.chat?.id) {
      history.replaceState(null, "", state.chat?.id ? `#/chat/${state.chat.id}` : "#/");
      toast("Wait for the reply to finish, or press Stop.");
    }
    return;
  }
  if (!m) {
    state.chat = null;
  } else if (state.chat?.id !== m[1]) {
    try {
      state.chat = await api(`/chats/${m[1]}`);
      if (state.chat.model && state.models.some((x) => x.id === state.chat.model)) setModel(state.chat.model);
    } catch (err) {
      report(err);
      state.chat = null;
      history.replaceState(null, "", "#/");
    }
  }
  renderChatList();
  renderThread();
  if (!touchScreen) $("input").focus();
}
window.addEventListener("hashchange", route);

function newChat() {
  if (state.controller) return toast("Wait for the reply to finish, or press Stop.");
  closeSidebar();
  if (location.hash !== "#/" && location.hash !== "") location.hash = "#/";
  else route();
}
$("new-chat").addEventListener("click", newChat);
$("new-chat-top").addEventListener("click", newChat);

// ---------- Messages ----------

const modelLabel = (id) => state.models.find((m) => m.id === id)?.name || (id ? id.replace(/^[^:]+:/, "") : "");

function nearBottom() {
  const box = $("messages");
  return box.scrollHeight - box.scrollTop - box.clientHeight < 120;
}

function scrollToEnd() {
  const box = $("messages");
  box.scrollTop = box.scrollHeight;
}

function renderThread() {
  const box = $("messages");
  box.replaceChildren();
  const messages = state.chat?.messages || [];
  if (!messages.length && !state.pending) {
    box.append(welcome());
    return;
  }
  const thread = el("div", { class: "thread" });
  messages.forEach((m, i) => thread.append(messageView(m, !state.pending && i === messages.length - 1)));
  if (state.pending) thread.append(pendingView());
  box.append(thread);
  scrollToEnd();
}

function welcome() {
  const hasModels = state.models.length > 0;
  return el("div", { class: "welcome" },
    el("h2", {}, hasModels ? "What's on your mind?" : "Welcome to Scimilir"),
    hasModels
      ? el("p", {}, "Using ", el("b", {}, modelLabel(state.model) || "no model yet"), ". Change it at the top.")
      : [
          el("p", {}, "To start, get a model that runs on this PC, or connect a cloud service."),
          el("button", { class: "primary", onclick: () => openSettings("local") }, "Get a model"),
        ]);
}

function messageView(m, isLast) {
  if (m.role === "user") return el("div", { class: "msg user" }, el("div", { class: "bubble" }, m.content));
  const tools = el("div", { class: "msg-tools" },
    el("button", { class: "icon-button", title: "Copy", "aria-label": "Copy reply", html: icons.copy,
      onclick: () => copyText(m.content.replace(/^\s*<think>[\s\S]*?(<\/think>|$)\s*/, "")) }),
    isLast ? el("button", { class: "icon-button", title: "Try again", "aria-label": "Write this reply again", html: icons.retry, onclick: regenerate }) : null,
    m.model ? el("span", { class: "msg-model" }, modelLabel(m.model)) : null);
  return el("div", { class: "msg assistant" }, el("div", { class: "bubble", html: Markdown.renderReply(m.content) }), tools);
}

function pendingText(p) {
  if (!p.thinking) return p.content;
  return "<think>" + p.thinking + (p.content ? "</think>\n\n" + p.content : "");
}

function pendingView() {
  const p = state.pending;
  const view = el("div", { class: "msg assistant", id: "pending" });
  const text = pendingText(p);
  if (text) view.append(el("div", { class: "bubble", html: Markdown.renderReply(text) }));
  if (!p.done && (!text || p.status)) {
    view.append(el("div", { class: "status" }, el("span", { class: "dots" }, el("i"), el("i"), el("i")), p.status || ""));
  }
  if (p.error) view.append(el("div", { class: "msg-error" }, p.error));
  return view;
}

let renderQueued = false;
function updatePending() {
  if (renderQueued) return;
  renderQueued = true;
  requestAnimationFrame(() => {
    renderQueued = false;
    const old = $("pending");
    if (!old || !state.pending) return;
    const stick = nearBottom();
    old.replaceWith(pendingView());
    if (stick) scrollToEnd();
  });
}

function setBusy(busy) {
  $("composer").classList.toggle("busy", busy);
  $("send").title = busy ? "Stop" : "Send";
  $("send").setAttribute("aria-label", busy ? "Stop" : "Send");
}

/** Sends a message (or asks again) and shows the reply as it arrives. */
async function stream(body) {
  state.pending = { content: "", thinking: "", status: "", error: null, done: false };
  state.controller = new AbortController();
  setBusy(true);
  renderThread();

  let chatId = body.chatId;
  try {
    const res = await fetch("/api/chats/send", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(body),
      signal: state.controller.signal,
    });
    if (res.status === 401) { showGate("login"); throw new LoggedOut(); }
    if (!res.ok) throw new Error((await res.json().catch(() => ({}))).message || `Something went wrong (${res.status}).`);

    const reader = res.body.getReader();
    const decoder = new TextDecoder();
    let buffer = "";
    for (;;) {
      const { value, done } = await reader.read();
      if (done) break;
      buffer += decoder.decode(value, { stream: true });
      let cut;
      while ((cut = buffer.indexOf("\n\n")) >= 0) {
        const block = buffer.slice(0, cut);
        buffer = buffer.slice(cut + 2);
        const name = block.match(/^event: (.*)$/m)?.[1];
        const data = JSON.parse(block.match(/^data: (.*)$/m)?.[1] || "{}");
        const p = state.pending;
        if (name === "chat") {
          chatId = data.id;
          if (!state.chat) state.chat = { id: data.id, title: data.title, messages: [] };
          state.chat.id = data.id;
          history.replaceState(null, "", `#/chat/${data.id}`);
          loadChats().catch(() => {});
        } else if (name === "text") {
          p.content += data.text;
          p.status = "";
        } else if (name === "thinking") {
          p.thinking += data.text;
          p.status = "";
        } else if (name === "status") {
          p.status = data.text;
        } else if (name === "error") {
          p.error = data.message;
        }
        updatePending();
      }
    }
  } catch (err) {
    if (err.name !== "AbortError" && !(err instanceof LoggedOut)) state.pending.error = err.message;
  }

  // Show the finished reply. The server has saved it already.
  const p = state.pending;
  const text = pendingText(p).trim() ? (p.thinking ? `<think>${p.thinking}</think>\n\n${p.content}` : p.content) : "";
  state.pending = null;
  state.controller = null;
  setBusy(false);
  if (state.chat && text) state.chat.messages.push({ role: "assistant", content: text, model: body.model });
  renderThread();
  if (p.error) {
    const box = $("messages").querySelector(".thread") || $("messages");
    box.append(el("div", { class: "msg-error" }, p.error));
    scrollToEnd();
  }
  if (chatId) loadChats().catch(() => {});
}

async function sendMessage() {
  const input = $("input");
  const content = input.value;
  if (!content.trim()) return;
  if (!state.model) {
    openModelPicker();
    return toast("Pick a model first.");
  }
  input.value = "";
  autoGrow();
  if (!state.chat) state.chat = { id: null, title: "", messages: [] };
  state.chat.messages.push({ role: "user", content });
  await stream({ chatId: state.chat.id, model: state.model, content, regenerate: false });
}

async function regenerate() {
  if (state.controller || !state.chat?.id) return;
  if (!state.model) return openModelPicker();
  while (state.chat.messages.length && state.chat.messages.at(-1).role === "assistant") state.chat.messages.pop();
  await stream({ chatId: state.chat.id, model: state.model, content: null, regenerate: true });
}

$("composer").addEventListener("submit", (e) => {
  e.preventDefault();
  if (state.controller) state.controller.abort();
  else sendMessage();
});

function autoGrow() {
  const t = $("input");
  t.style.height = "auto";
  t.style.height = Math.min(t.scrollHeight, innerHeight * 0.4) + "px";
}
$("input").addEventListener("input", autoGrow);
$("input").addEventListener("keydown", (e) => {
  // On a computer, Enter sends and Shift+Enter starts a new line. On a phone, the Enter key adds a line.
  if (e.key === "Enter" && !e.shiftKey && !touchScreen && !e.isComposing) {
    e.preventDefault();
    if (!state.controller) sendMessage();
  }
});

$("messages").addEventListener("click", (e) => {
  const button = e.target.closest("[data-copy-code]");
  if (button) copyText(button.closest(".code").querySelector("code").textContent);
});

// ---------- Sidebar on phones ----------

function openSidebar() {
  $("sidebar").classList.add("open");
  $("scrim").hidden = false;
}
function closeSidebar() {
  $("sidebar").classList.remove("open");
  $("scrim").hidden = true;
}
$("open-sidebar").addEventListener("click", openSidebar);
$("scrim").addEventListener("click", closeSidebar);

// ---------- Model picker ----------

async function loadModels() {
  const data = await api("/models");
  state.models = data.models;
  for (const problem of data.problems) toast(problem, 6000);
  let pick = state.model;
  if (!state.models.some((m) => m.id === pick)) pick = null;
  pick ??= state.models.some((m) => m.id === data.defaultModel) ? data.defaultModel : state.models[0]?.id ?? null;
  setModel(pick);
}

function setModel(id) {
  state.model = id;
  $("model-name").textContent = id ? modelLabel(id) : state.models.length ? "Pick a model" : "No models yet";
}

function openModelPicker() {
  $("model-search").value = "";
  renderModelList();
  $("model-dialog").showModal();
  if (!touchScreen) $("model-search").focus();
}

function renderModelList() {
  const q = $("model-search").value.trim().toLowerCase();
  const list = $("model-list");
  list.replaceChildren();
  const matches = state.models.filter((m) => !q || m.name.toLowerCase().includes(q) || m.id.toLowerCase().includes(q));
  if (!state.models.length) {
    list.append(el("div", { class: "model-empty" },
      el("p", {}, "No models yet. Get one that runs on this PC, or add an OpenRouter key for cloud models."),
      el("div", { class: "row-end" },
        el("button", { class: "secondary", onclick: () => { $("model-dialog").close(); openSettings("cloud"); } }, "Add cloud key"),
        el("button", { class: "primary", onclick: () => { $("model-dialog").close(); openSettings("local"); } }, "Get a model"))));
    return;
  }
  if (!matches.length) list.append(el("div", { class: "model-empty" }, "No models match."));
  let group = null;
  for (const m of matches.slice(0, 300)) {
    if (m.group !== group) {
      group = m.group;
      list.append(el("div", { class: "model-group" }, group));
    }
    list.append(el("button", {
      class: "model-item" + (m.id === state.model ? " selected" : ""),
      onclick: () => { setModel(m.id); $("model-dialog").close(); if (!state.chat?.messages?.length) renderThread(); },
    }, el("b", {}, m.name), el("small", {}, m.detail || m.id.replace(/^[^:]+:/, ""))));
  }
}

$("model-button").addEventListener("click", openModelPicker);
$("model-search").addEventListener("input", renderModelList);

// Dialogs close with their × button, or by tapping outside them.
for (const dialog of document.querySelectorAll("dialog")) {
  dialog.addEventListener("click", (e) => {
    if (e.target === dialog || e.target.closest("[data-close]")) dialog.close();
  });
}

// ---------- Settings ----------

function openSettings(tab = "general") {
  closeSidebar();
  $("settings-dialog").showModal();
  showTab(tab);
}
$("open-settings").addEventListener("click", () => openSettings());
$("settings-dialog").addEventListener("close", () => {
  clearInterval(downloadTimer);
  downloadTimer = null;
  loadModels().catch(report);
});

for (const tab of document.querySelectorAll("[data-tab]")) tab.addEventListener("click", () => showTab(tab.dataset.tab));

function showTab(name) {
  for (const t of document.querySelectorAll("[data-tab]")) t.setAttribute("aria-selected", String(t.dataset.tab === name));
  for (const p of document.querySelectorAll("[data-panel]")) p.hidden = p.dataset.panel !== name;
  const loaders = { general: loadSettings, local: loadLocal, cloud: loadSettings, phone: loadPhone };
  loaders[name]?.().catch(report);
}

async function loadSettings() {
  const s = await api("/settings");
  $("set-system").value = s.systemPrompt;
  $("set-temp").value = s.temperature;
  $("set-temp-out").textContent = Number(s.temperature).toFixed(2);
  $("set-browser").checked = s.openBrowserOnStart;
  $("set-context").value = s.contextSize;
  $("set-maxreply").value = s.maxReplyTokens;
  $("set-gpu").value = s.gpuLayers;
  $("set-key").value = "";
  $("key-state").textContent = s.hasOpenRouterKey
    ? `A key is saved (${s.openRouterKeyHint || "hidden"}). Paste a new one to replace it.`
    : "No key saved yet.";
  $("remove-key").hidden = !s.hasOpenRouterKey;
}
$("set-temp").addEventListener("input", () => ($("set-temp-out").textContent = Number($("set-temp").value).toFixed(2)));

async function saveSettings(body, message = "Saved") {
  try {
    await api("/settings", { method: "PUT", body });
    toast(message);
    await loadSettings();
  } catch (err) { report(err); }
}

$("save-general").addEventListener("click", () => saveSettings({
  systemPrompt: $("set-system").value,
  temperature: Number($("set-temp").value),
  openBrowserOnStart: $("set-browser").checked,
}));
$("save-local").addEventListener("click", () => saveSettings({
  contextSize: Number($("set-context").value),
  maxReplyTokens: Number($("set-maxreply").value),
  gpuLayers: Number($("set-gpu").value),
}, "Saved. The model reloads with these settings on your next message."));
$("save-key").addEventListener("click", async () => {
  const key = $("set-key").value.trim();
  if (!key) return toast("Paste your key first.");
  await saveSettings({ openRouterKey: key }, "Key saved. OpenRouter models are now in the model list.");
});
$("remove-key").addEventListener("click", async () => {
  if (confirm("Remove your OpenRouter key from Scimilir?")) await saveSettings({ openRouterKey: "" }, "Key removed.");
});

$("password-form").addEventListener("submit", async (e) => {
  e.preventDefault();
  try {
    await api("/password", { method: "POST", body: { current: $("pw-current").value, next: $("pw-next").value } });
    $("pw-current").value = $("pw-next").value = "";
    toast("Password changed.");
  } catch (err) { report(err); }
});
$("logout").addEventListener("click", async () => {
  await api("/logout", { method: "POST" }).catch(() => {});
  $("settings-dialog").close();
  showGate("login");
});

// ----- On this PC: models and downloads -----

const size = (bytes) => bytes >= 2 ** 30 ? `${(bytes / 2 ** 30).toFixed(1)} GB`
  : bytes >= 2 ** 20 ? `${Math.round(bytes / 2 ** 20)} MB`
  : `${Math.max(1, Math.round(bytes / 1024))} KB`;
let downloadTimer = null;
let knownDone = new Set();

async function loadLocal() {
  const s = await api("/settings");
  $("set-context").value = s.contextSize;
  $("set-maxreply").value = s.maxReplyTokens;
  $("set-gpu").value = s.gpuLayers;

  const info = await api("/local");
  const status = $("local-status");
  status.replaceChildren();
  // The engine reports lines like "using device Vulkan0 (NVIDIA GeForce RTX 3060) - 11264 MiB free"
  // and "offloaded 29/29 layers to GPU"; turn them into plain words.
  const device = info.device.map((line) => {
    const card = line.match(/using device \S+ \((.+?)\)/);
    if (card) return `Graphics card: ${card[1]}`;
    const layers = line.match(/offloaded (\d+)\/(\d+) layers/);
    if (layers && layers[1] === "0") return "None of the model is on the graphics card, so it runs on the processor (slower).";
    if (layers) return `${layers[1]} of ${layers[2]} layers are on the graphics card${layers[1] === layers[2] ? " (all of them: fastest)" : ""}`;
    return null;
  }).filter(Boolean);
  if (info.loaded && !device.length) device.push("No graphics card is being used, so the model runs on the processor (slower).");
  status.append(...[
    el("div", {}, info.loaded
      ? `Loaded now: ${info.loaded.replace(/\.gguf$/i, "")}${info.onProcessorOnly ? " (on the processor)" : ""}`
      : "No model loaded. One loads when you send it a message."),
    ...device.map((d) => el("div", {}, d)),
    el("div", {}, `Free disk space: ${size(info.freeBytes)}`),
    el("small", {}, `Model folder: ${info.folder}`),
    info.loaded ? el("div", { class: "row-end" },
      el("button", { class: "secondary", onclick: async () => { await api("/local/unload", { method: "POST" }).catch(report); loadLocal(); } }, "Unload (free memory)")) : null,
  ].filter(Boolean));

  const list = $("local-models");
  list.replaceChildren();
  if (!info.models.length) list.append(el("div", { class: "row" }, el("span", { class: "grow hint" }, "None yet. Download one below.")));
  for (const m of info.models) {
    list.append(el("div", { class: "row" },
      el("div", { class: "grow" }, el("b", {}, m.name), m.loaded ? el("span", { class: "badge" }, "loaded") : null, el("small", {}, size(m.size))),
      el("button", { class: "danger-button", onclick: () => deleteModel(m) }, "Delete")));
  }

  const chips = $("starter-chips");
  if (!chips.children.length) {
    for (const s of STARTERS) chips.append(el("button", { class: "chip", onclick: () => showRepoFiles(s.repo) }, s.label));
  }
  await refreshDownloads();
}

async function deleteModel(m) {
  if (!confirm(`Delete ${m.name} (${size(m.size)}) from this PC?`)) return;
  try {
    await api(`/local/models/${encodeURIComponent(m.file)}`, { method: "DELETE" });
    toast("Deleted.");
    await loadLocal();
  } catch (err) { report(err); }
}

async function refreshDownloads() {
  const jobs = await api("/downloads");
  const list = $("download-list");
  list.replaceChildren();
  if (jobs.length) list.append(el("h3", {}, "Downloads"));
  for (const j of jobs) {
    const pct = j.total ? Math.floor((j.done / j.total) * 100) : 0;
    const words = { downloading: `${size(j.done)} of ${size(j.total)} (${pct}%)`, done: "Finished", failed: `Failed: ${j.error}`, cancelled: "Cancelled" }[j.state];
    list.append(el("div", { class: "row" },
      el("div", { class: "grow" }, el("b", {}, j.name), el("small", {}, words),
        j.state === "downloading" ? el("div", { class: "bar" }, el("i", { style: `width:${pct}%` })) : null),
      j.state === "downloading"
        ? el("button", { class: "secondary", onclick: () => api(`/downloads/${j.id}/cancel`, { method: "POST" }).then(refreshDownloads, report) }, "Cancel")
        : null));
    if (j.state === "done" && !knownDone.has(j.id)) {
      knownDone.add(j.id);
      toast(`${j.name} is ready. Pick it at the top of the chat.`);
      loadModels().catch(() => {});
      if (!$("settings-dialog").querySelector("[data-panel=local]").hidden) loadLocal().catch(() => {});
    }
  }
  if (jobs.some((j) => j.state !== "downloading")) {
    list.append(el("div", { class: "row-end" },
      el("button", { class: "secondary", onclick: () => api("/downloads/clear", { method: "POST" }).then(refreshDownloads, report) }, "Clear finished")));
  }
  const active = jobs.some((j) => j.state === "downloading");
  if (active && !downloadTimer) downloadTimer = setInterval(() => refreshDownloads().catch(() => {}), 1500);
  if (!active && downloadTimer) { clearInterval(downloadTimer); downloadTimer = null; }
}

$("hf-search-form").addEventListener("submit", async (e) => {
  e.preventDefault();
  const q = $("hf-search").value.trim();
  if (!q) return;
  const box = $("hf-results");
  box.replaceChildren(el("div", { class: "row hint" }, "Searching…"));
  try {
    const repos = await api(`/hf/search?q=${encodeURIComponent(q)}`);
    box.replaceChildren();
    if (!repos.length) box.append(el("div", { class: "row hint" }, "Nothing found. Try a shorter word, like “qwen”."));
    for (const r of repos) {
      box.append(el("button", { class: "row", style: "text-align:left", onclick: () => showRepoFiles(r.id) },
        el("div", { class: "grow" }, el("b", {}, r.id), el("small", {}, `${r.downloads.toLocaleString()} downloads · ${r.likes} likes`))));
    }
  } catch (err) {
    box.replaceChildren();
    report(err);
  }
});

async function showRepoFiles(repo) {
  const box = $("hf-results");
  box.replaceChildren(el("div", { class: "row hint" }, `Getting the files in ${repo}…`));
  try {
    const files = await api(`/hf/files?repo=${encodeURIComponent(repo)}`);
    box.replaceChildren(el("div", { class: "row-end", style: "justify-content:space-between;align-items:center" },
      el("b", {}, repo),
      el("a", { href: `https://huggingface.co/${repo}`, target: "_blank", rel: "noopener" }, "About this model ↗")));
    if (!files.length) box.append(el("div", { class: "row hint" }, "This one has no .gguf model files Scimilir can run."));
    for (const f of files) {
      const good = /Q4_K_M/i.test(f.name);
      box.append(el("div", { class: "row" },
        el("div", { class: "grow" }, f.name, good ? el("span", { class: "badge" }, "good start") : null,
          el("small", {}, size(f.size) + (f.paths.length > 1 ? ` · ${f.paths.length} parts` : ""))),
        el("button", { class: good ? "primary" : "secondary", onclick: () => startDownload(repo, f) }, "Download")));
    }
    box.scrollIntoView({ block: "nearest", behavior: "smooth" });
  } catch (err) {
    box.replaceChildren();
    report(err);
  }
}

async function startDownload(repo, f) {
  try {
    await api("/hf/download", { method: "POST", body: { repo, file: f.name } });
    toast(`Downloading ${f.name}. You can keep chatting meanwhile.`);
    await refreshDownloads();
    $("download-list").scrollIntoView({ block: "nearest", behavior: "smooth" });
  } catch (err) { report(err); }
}

// ----- Phone -----

async function loadPhone() {
  const { urls } = await api("/connect");
  const box = $("phone-links");
  box.replaceChildren();
  if (!urls.length) {
    box.append(el("p", { class: "note" }, "This PC doesn't seem to be on a home network right now. Check it's connected to your Wi-Fi or router."));
    return;
  }
  for (const url of urls) {
    box.append(el("div", { class: "qr" },
      el("img", { src: `/api/connect/qr?url=${encodeURIComponent(url)}`, alt: `QR code for ${url}` }),
      el("div", {}, el("small", {}, "Address"), el("br"), el("code", {}, url))));
  }
}

boot();
