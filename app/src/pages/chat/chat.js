// 消息内容是服务器 HTML，里面的点击、右键、复制在这里委托后交给 Rust。
(() => {
  if (window.zulipChat) return;
  const panes = new Map();

  const paneOf = (node) => {
    const el = node instanceof Element ? node : node && node.parentElement;
    if (!el) return null;
    for (const [id, send] of panes) {
      if (el.closest(`#${id}`)) return send;
    }
    return null;
  };
  const msgOf = (el) => {
    const row = el.closest("[data-msg]");
    return row ? Number(row.dataset.msg) : null;
  };

  const serialize = (fragment) => {
    const box = document.createElement("div");
    box.appendChild(fragment);
    return box.innerHTML;
  };

  // 选中内容：跨多条消息时每条前面加“名字: 时间”，消息之间空一行
  const selectionPayload = (sel) => {
    const range = sel.getRangeAt(0);
    const start = range.startContainer;
    const root = (start instanceof Element ? start : start.parentElement).closest(".chat-list");
    const rows = root
      ? [...root.querySelectorAll("[data-msg]")].filter((r) => range.intersectsNode(r))
      : [];
    if (rows.length < 2) {
      return { text: sel.toString(), html: serialize(range.cloneContents()) };
    }
    const texts = [];
    const htmls = [];
    for (const row of rows) {
      const part = range.cloneRange();
      if (row.contains(range.startContainer)) part.setEndAfter(row);
      else if (row.contains(range.endContainer)) part.setStartBefore(row);
      else part.selectNode(row);
      const box = document.createElement("div");
      box.appendChild(part.cloneContents());
      box.querySelectorAll(".msg-avatar, .msg-name, .msg-time, .msg-reactions, .table-tools").forEach((e) => e.remove());
      const content = box.innerText.trim();
      if (!content) continue;
      const head = row.dataset.head;
      texts.push(head ? `${head}\n${content}` : content);
      htmls.push(box.innerHTML);
    }
    return { text: texts.join("\n\n"), html: htmls.join("") };
  };

  // 捕获阶段拦截：链接不能进入框架默认的打开方式
  const onClick = (e) => {
    const send = paneOf(e.target);
    if (!send || !(e.target instanceof Element)) return;
    const t = e.target;
    const content = t.closest(".msg-content");
    if (!content) return;
    const consume = () => {
      e.preventDefault();
      e.stopPropagation();
    };
    // 多选时点击只用来勾选
    if (t.closest(".chat-pane.multi")) {
      if (t.closest("a[href]")) e.preventDefault();
      return;
    }
    const more = t.closest(".table-more");
    if (more) {
      consume();
      send({
        t: "table",
        msg: msgOf(t),
        index: Number(more.closest(".table-host").dataset.table),
        x: e.clientX,
        y: e.clientY,
      });
      return;
    }
    const spoiler = t.closest(".spoiler-head");
    if (spoiler) {
      consume();
      spoiler.parentElement.classList.toggle("open");
      return;
    }
    const image = t.closest(".img-preview");
    if (image) {
      consume();
      send({ t: "image", msg: msgOf(t), index: Number(image.dataset.index) });
      return;
    }
    const quote = t.closest(".quote-head");
    if (quote) {
      consume();
      send({ t: "link", msg: msgOf(t), href: quote.dataset.href });
      return;
    }
    const mention = t.closest(".mention");
    if (mention) {
      consume();
      send({ t: "mention", user: mention.dataset.userId || null, text: mention.textContent });
      return;
    }
    const a = t.closest("a[href]");
    if (a) {
      consume();
      send({ t: "link", msg: msgOf(t), href: a.getAttribute("href") });
    }
  };

  const onContextMenu = (e) => {
    const send = paneOf(e.target);
    if (!send || !(e.target instanceof Element)) return;
    const t = e.target;
    if (t.closest("input, textarea, [contenteditable=true], .chat-composer, .more-panel, .topic-sidebar, .chat-head, .file-card, .multi-bar")) return;
    e.preventDefault();
    if (!t.closest(".chat-list")) return;
    const hit = t.closest(".bubble, .msg-reactions, .msg-annotation");
    const sender = t.closest(".msg-avatar, .msg-name");
    const sel = document.getSelection();
    const row = t.closest("[data-msg]");
    let text = null;
    let html = null;
    if (sel && !sel.isCollapsed && row && sel.containsNode(row, true)) {
      const payload = selectionPayload(sel);
      text = payload.text;
      html = payload.html;
    }
    const image = t.closest(".img-preview");
    send({
      t: "menu",
      msg: (hit || sender) && row ? Number(row.dataset.msg) : null,
      sender: Boolean(sender),
      x: e.clientX,
      y: e.clientY,
      image: image ? Number(image.dataset.index) : null,
      text,
      html,
    });
  };

  const onCopy = (e) => {
    const sel = document.getSelection();
    if (!sel || sel.isCollapsed) return;
    const send = paneOf(sel.anchorNode);
    if (!send) return;
    const anchor = sel.anchorNode instanceof Element ? sel.anchorNode : sel.anchorNode.parentElement;
    if (anchor.closest("input, textarea, [contenteditable=true]") || !anchor.closest(".chat-list")) return;
    const { text, html } = selectionPayload(sel);
    e.clipboardData.setData("text/plain", text);
    e.clipboardData.setData("text/html", html);
    e.preventDefault();
    send({ t: "copy", text, html });
  };

  const onMouseUp = (e) => {
    const send = paneOf(e.target);
    if (!send || !(e.target instanceof Element)) return;
    const msg = msgOf(e.target);
    if (msg === null) return;
    send({
      t: "gesture",
      msg,
      button: e.button,
      clicks: e.detail,
      ctrl: e.ctrlKey,
      alt: e.altKey,
      shift: e.shiftKey,
      meta: e.metaKey,
    });
  };

  const onKeyDown = (e) => {
    if (e.key !== "Escape" || e.defaultPrevented) return;
    for (const send of panes.values()) send({ t: "esc" });
  };

  window.zulipChat = {
    attach(id, send) {
      if (panes.size === 0) {
        document.addEventListener("click", onClick, true);
        document.addEventListener("contextmenu", onContextMenu);
        document.addEventListener("copy", onCopy);
        document.addEventListener("mouseup", onMouseUp);
        document.addEventListener("keydown", onKeyDown);
      }
      panes.set(id, send);
      return new Promise((resolve) => panes.set(id, Object.assign(send, { close: resolve })));
    },
    detach(id) {
      const send = panes.get(id);
      panes.delete(id);
      if (send) send.close();
      if (panes.size === 0) {
        document.removeEventListener("click", onClick, true);
        document.removeEventListener("contextmenu", onContextMenu);
        document.removeEventListener("copy", onCopy);
        document.removeEventListener("mouseup", onMouseUp);
        document.removeEventListener("keydown", onKeyDown);
      }
    },
    setImage(msg, url, local) {
      const row = document.querySelector(`[data-msg="${msg}"]`);
      if (!row) return;
      for (const img of row.querySelectorAll("img[data-remote]")) {
        if (img.dataset.remote === url) img.src = local;
      }
    },
    failImage(msg, url) {
      const row = document.querySelector(`[data-msg="${msg}"]`);
      if (!row) return;
      for (const img of row.querySelectorAll("img[data-remote]")) {
        if (img.dataset.remote === url && img.parentElement) img.parentElement.classList.add("failed");
      }
    },
  };
})();
