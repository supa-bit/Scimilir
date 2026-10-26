// Turns a model's reply (written in "Markdown": **bold**, `code`, lists...) into
// HTML. Everything is escaped first, so nothing in a reply can run as code on
// the page; only the formatting below is added back.
"use strict";

const Markdown = (() => {
  const escape = (s) => s.replace(/[&<>"']/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" })[c]);

  function inline(text) {
    // Code and web addresses are set aside first, so their * and _ aren't read as formatting.
    const kept = [];
    const keep = (html) => {
      kept.push(html);
      return `\u0000${kept.length - 1}\u0000`;
    };
    const link = (url, label) => keep(`<a href="${url}" target="_blank" rel="noopener noreferrer">${label}</a>`);
    let s = escape(text)
      .replace(/`([^`\n]+)`/g, (_, code) => keep(`<code>${code}</code>`))
      .replace(/\[([^\]\n]+)\]\((https?:\/\/[^\s)]+)\)/g, (_, label, url) => link(url, label))
      .replace(/(^|[\s(])(https?:\/\/[^\s<)]*[^\s<).,;:!?])/g, (_, before, url) => before + link(url, url));
    s = s
      .replace(/\*\*(?=\S)([\s\S]*?\S)\*\*/g, "<strong>$1</strong>")
      .replace(/__(?=\S)([\s\S]*?\S)__/g, "<strong>$1</strong>")
      .replace(/(^|[^*\w])\*(?=\S)([^*\n]*?\S)\*(?!\*)/g, "$1<em>$2</em>")
      .replace(/(^|[^_\w])_(?=\S)([^_\n]*?\S)_(?!\w)/g, "$1<em>$2</em>")
      .replace(/~~(?=\S)([^~\n]*?\S)~~/g, "<del>$1</del>");
    return s.replace(/\u0000(\d+)\u0000/g, (_, i) => kept[i]);
  }

  const isFence = (line) => /^\s*(```|~~~)/.test(line);
  const listMarker = /^(\s*)([-*+]|\d+[.)])\s+(.*)$/;
  const tableRule = /^\s*\|?\s*:?-{2,}:?\s*(\|\s*:?-{2,}:?\s*)*\|?\s*$/;
  const cells = (line) => line.trim().replace(/^\|/, "").replace(/\|$/, "").split("|").map((c) => c.trim());

  function startsBlock(lines, i) {
    const line = lines[i];
    return isFence(line) || /^#{1,6}\s/.test(line) || /^\s*>/.test(line) || listMarker.test(line)
      || /^\s*([-*_])(\s*\1){2,}\s*$/.test(line)
      || (line.includes("|") && i + 1 < lines.length && tableRule.test(lines[i + 1]));
  }

  function codeBlock(code, lang) {
    return `<div class="code"><div class="code-head"><span>${escape(lang || "code")}</span>`
      + `<button type="button" data-copy-code>Copy</button></div><pre><code>${escape(code)}</code></pre></div>`;
  }

  function list(lines, i) {
    const first = lines[i].match(listMarker);
    const ordered = /\d/.test(first[2]);
    const baseIndent = first[1].length;
    const start = ordered ? parseInt(first[2], 10) : 1;
    const items = [];
    const isItem = (line) => {
      const m = line?.match(listMarker);
      return m && m[1].length <= baseIndent + 1 && /\d/.test(m[2]) === ordered ? m : null;
    };
    while (i < lines.length) {
      const m = isItem(lines[i]);
      if (m) {
        items.push([m[3]]);
        i++;
      } else if (lines[i].trim() !== "" && /^\s+/.test(lines[i])) {
        items[items.length - 1].push(lines[i].replace(/^\s{1,4}/, ""));
        i++;
      } else if (lines[i].trim() === "" && isItem(lines[i + 1])) {
        i++; // a blank line between items
      } else {
        break;
      }
    }
    const html = items.map((parts) => {
      // Lines indented under an item (like a nested list) are rendered as their own blocks.
      const [head, ...rest] = parts;
      return `<li>${inline(head)}${rest.length ? render(rest.join("\n")) : ""}</li>`;
    }).join("");
    const tag = ordered ? "ol" : "ul";
    return [`<${tag}${ordered && start !== 1 ? ` start="${start}"` : ""}>${html}</${tag}>`, i];
  }

  function render(src) {
    const lines = src.replace(/\r\n?/g, "\n").split("\n");
    const out = [];
    let i = 0;
    while (i < lines.length) {
      const line = lines[i];
      if (isFence(line)) {
        const fence = line.trim().slice(0, 3);
        const lang = line.trim().slice(3).trim().split(/\s/)[0];
        const body = [];
        i++;
        while (i < lines.length && !lines[i].trim().startsWith(fence)) body.push(lines[i++]);
        i++;
        out.push(codeBlock(body.join("\n"), lang));
        continue;
      }
      if (line.trim() === "") { i++; continue; }
      const heading = line.match(/^(#{1,6})\s+(.*?)\s*#*\s*$/);
      if (heading) {
        const level = Math.min(heading[1].length + 1, 6); // a reply's "#" is a sub-heading on the page
        out.push(`<h${level}>${inline(heading[2])}</h${level}>`);
        i++;
        continue;
      }
      if (/^\s*([-*_])(\s*\1){2,}\s*$/.test(line)) { out.push("<hr>"); i++; continue; }
      if (/^\s*>/.test(line)) {
        const quote = [];
        while (i < lines.length && /^\s*>/.test(lines[i])) quote.push(lines[i++].replace(/^\s*>\s?/, ""));
        out.push(`<blockquote>${render(quote.join("\n"))}</blockquote>`);
        continue;
      }
      if (listMarker.test(line)) {
        const [html, next] = list(lines, i);
        out.push(html);
        i = next;
        continue;
      }
      if (line.includes("|") && i + 1 < lines.length && tableRule.test(lines[i + 1])) {
        const head = cells(line);
        i += 2;
        const rows = [];
        while (i < lines.length && lines[i].includes("|") && lines[i].trim() !== "") rows.push(cells(lines[i++]));
        out.push(`<table><thead><tr>${head.map((c) => `<th>${inline(c)}</th>`).join("")}</tr></thead><tbody>`
          + rows.map((r) => `<tr>${r.map((c) => `<td>${inline(c)}</td>`).join("")}</tr>`).join("") + "</tbody></table>");
        continue;
      }
      const para = [line];
      i++;
      while (i < lines.length && lines[i].trim() !== "" && !startsBlock(lines, i)) para.push(lines[i++]);
      out.push(`<p>${para.map(inline).join("<br>")}</p>`);
    }
    return out.join("");
  }

  /**
   * A reply may start with the model's "thinking" in <think>...</think>.
   * That part is shown folded away, apart from the answer.
   */
  function renderReply(content) {
    const m = content.match(/^\s*<think>([\s\S]*?)(<\/think>|$)/);
    if (!m) return render(content);
    const finished = m[2] !== "";
    const thinking = m[1].trim();
    const answer = content.slice(m[0].length);
    const box = thinking
      ? `<details class="think"${finished ? "" : " open"}><summary>${finished ? "Thought process" : "Thinking…"}</summary><div>${render(thinking)}</div></details>`
      : "";
    return box + render(answer);
  }

  return { render, renderReply, escape };
})();

if (typeof module !== "undefined") module.exports = Markdown;
