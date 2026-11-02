/* 下拉選單統一成深色圓角清單（照組員截圖），各瀏覽器看起來一樣。
   原生 <select> 留在背後：值、change 事件與既有程式照舊；之後重畫出來的 select 也會自動套用。 */
(() => {
  let current = null;

  function close() {
    if (!current) return;
    current.list.hidden = true;
    current.wrap.classList.remove("open");
    current = null;
  }

  function enhance(sel) {
    if (sel.dataset.dd) return;
    sel.dataset.dd = "1";
    const wrap = document.createElement("div");
    wrap.className = "dd";
    sel.parentNode.insertBefore(wrap, sel);
    wrap.appendChild(sel);
    const btn = document.createElement("button");
    btn.type = "button";
    btn.className = "dd-btn";
    btn.setAttribute("aria-haspopup", "listbox");
    const list = document.createElement("div");
    list.className = "dd-list";
    list.setAttribute("role", "listbox");
    list.hidden = true;
    wrap.append(btn, list);

    const sync = () => { btn.textContent = sel.selectedOptions[0]?.textContent ?? ""; };
    const pick = (i) => {
      if (i < 0 || i >= sel.options.length) return;
      sel.selectedIndex = i;
      sel.dispatchEvent(new Event("change", { bubbles: true }));
      sync();
    };
    const build = () => {
      list.replaceChildren(...[...sel.options].map((o, i) => {
        const el = document.createElement("div");
        el.className = "dd-opt" + (o.selected ? " on" : "");
        el.setAttribute("role", "option");
        el.dataset.i = i;
        el.textContent = o.textContent;
        return el;
      }));
    };
    const open = () => {
      close();
      build();
      list.hidden = false;
      wrap.classList.add("open");
      current = { wrap, list, sel, pick };
      list.querySelector(".on")?.scrollIntoView({ block: "nearest" });
    };

    btn.addEventListener("click", (e) => { e.preventDefault(); e.stopPropagation(); if (current?.wrap === wrap) close(); else { sync(); open(); } });
    list.addEventListener("click", (e) => {
      e.stopPropagation();
      const o = e.target.closest(".dd-opt");
      if (o) { pick(+o.dataset.i); close(); btn.focus(); }
    });
    btn.addEventListener("keydown", (e) => {
      if (e.key === "ArrowDown" || e.key === "ArrowUp") {
        e.preventDefault();
        pick(sel.selectedIndex + (e.key === "ArrowDown" ? 1 : -1));
        if (current?.wrap === wrap) build();
      }
    });
    sel.addEventListener("change", sync);
    new MutationObserver(sync).observe(sel, { childList: true, subtree: true, characterData: true });
    sync();
  }

  const scan = (root) => root.querySelectorAll?.("select.pill").forEach(enhance);
  document.addEventListener("click", close);
  addEventListener("keydown", (e) => { if (e.key === "Escape") close(); });
  addEventListener("resize", close);
  new MutationObserver((ms) => ms.forEach((m) => m.addedNodes.forEach((n) => { if (n.nodeType === 1) { if (n.matches("select.pill")) enhance(n); else scan(n); } })))
    .observe(document.body, { childList: true, subtree: true });
  scan(document);
})();
