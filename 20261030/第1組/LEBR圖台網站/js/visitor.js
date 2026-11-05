/* 訪客名字（不是真的登入）：彈出視窗輸入名字，Dashboard 的註記用它當註記人。
   名字只存在這台電腦的瀏覽器（勾「記住我」存 localStorage，否則只到關掉分頁為止），不會送到任何伺服器。 */
(() => {
  const KEY = "lebr-note-author";
  const zh = (document.documentElement.lang || "").startsWith("zh");
  const T = zh
    ? { title: "登入", label: "你的名字", remember: "記住我", go: "登入", guest: "先以訪客身分瀏覽", logout: "登出", need: "請輸入名字", hi: (n) => `${n}，歡迎！之後的註記會顯示你的名字` }
    : { title: "Login", label: "Your name", remember: "Remember me", go: "Login", guest: "Continue as guest", logout: "Log out", need: "Please enter your name", hi: (n) => `Welcome, ${n}! Your notes will show your name.` };

  const read = (store) => { try { return store.getItem(KEY) || ""; } catch (e) { return ""; } };
  const name = () => read(sessionStorage) || read(localStorage);
  function save(value, remember) {
    try {
      if (value) sessionStorage.setItem(KEY, value); else sessionStorage.removeItem(KEY);
      if (value && remember) localStorage.setItem(KEY, value); else localStorage.removeItem(KEY);
    } catch (e) { /* 私密視窗等無法儲存：這次瀏覽仍可用 */ }
    dispatchEvent(new CustomEvent("visitorchange", { detail: value }));
  }

  const bg = document.createElement("div");
  bg.className = "login-bg";
  bg.hidden = true;
  bg.innerHTML = `
    <form class="login-card" role="dialog" aria-modal="true" aria-labelledby="login-title" autocomplete="off">
      <button type="button" class="login-x" data-act="close" aria-label="Close">✕</button>
      <h2 id="login-title">${T.title}</h2>
      <label class="login-field">
        <input type="text" name="visitor" maxlength="40" placeholder=" " autocomplete="off">
        <span>${T.label}</span>
        <svg viewBox="0 0 24 24" aria-hidden="true"><circle cx="12" cy="8" r="4" fill="none" stroke="currentColor" stroke-width="1.8"/><path d="M4 20c1.5-4 4.5-6 8-6s6.5 2 8 6" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round"/></svg>
      </label>
      <div class="login-row">
        <label class="login-check"><input type="checkbox" name="remember" checked> ${T.remember}</label>
        <a href="#" data-act="logout" hidden>${T.logout}</a>
      </div>
      <button type="submit" class="login-submit">${T.go}</button>
      <p class="login-foot"><a href="#" data-act="close">${T.guest}</a></p>
    </form>`;
  document.body.appendChild(bg);
  const form = bg.querySelector("form"), input = form.elements.visitor;

  function open() {
    input.value = name();
    form.querySelector("[data-act=logout]").hidden = !name();
    bg.hidden = false;
    requestAnimationFrame(() => bg.classList.add("open"));
    setTimeout(() => input.focus(), 150);
  }
  function close() {
    bg.classList.remove("open");
    setTimeout(() => { bg.hidden = true; }, 250);
  }
  const say = (msg) => (typeof toast === "function" ? toast(msg, 3500) : null);

  form.addEventListener("submit", (e) => {
    e.preventDefault();
    const v = input.value.trim();
    if (!v) { input.focus(); return say(T.need); }
    save(v, form.elements.remember.checked);
    close();
    say(T.hi(v));
  });
  bg.addEventListener("click", (e) => {
    const act = e.target.closest("[data-act]")?.dataset.act;
    if (e.target === bg || act === "close") { e.preventDefault(); close(); }
    if (act === "logout") { e.preventDefault(); save("", false); close(); }
  });
  addEventListener("keydown", (e) => { if (e.key === "Escape" && !bg.hidden) close(); });

  window.visitor = { name, open };
})();
