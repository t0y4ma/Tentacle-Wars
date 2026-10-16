// ブラウザ版の文字入力（Tentacle Wars）
// uGUI の入力欄はブラウザでは日本語入力などが使えないため、押した入力欄の上に本物の HTML の入力欄を重ねて入力させる
mergeInto(LibraryManager.library, {
  // 入力欄を押し下げた時に呼ぶ。指を離した瞬間（ユーザー操作の中）に HTML の入力欄を出してフォーカスする
  // x, y, w, h は Unity の画面座標（左下原点・ピクセル）
  TW_ArmInput: function (goPtr, id, textPtr, x, y, w, h, font, multiline, maxLen, password, colorsPtr) {
    var go = UTF8ToString(goPtr), text = UTF8ToString(textPtr), colors = UTF8ToString(colorsPtr).split(',');
    var send = (typeof SendMessage !== 'undefined') ? SendMessage : Module.SendMessage;
    var fire = function () {
      document.removeEventListener('pointerup', fire, true);
      if (window.twInput) window.twInput.done(false);
      var canvas = (typeof Module !== 'undefined' && Module.canvas) ? Module.canvas : document.querySelector('canvas');
      var r = canvas.getBoundingClientRect();
      var sx = r.width / canvas.width, sy = r.height / canvas.height;
      var el = document.createElement(multiline ? 'textarea' : 'input');
      if (!multiline) el.type = password ? 'password' : 'text';
      if (maxLen > 0) el.maxLength = maxLen;
      el.value = text;
      el.autocomplete = 'off';
      el.spellcheck = false;
      var s = el.style;
      s.position = 'fixed';
      s.left = (r.left + x * sx) + 'px';
      s.top = (r.top + (canvas.height - y - h) * sy) + 'px';
      s.width = (w * sx) + 'px';
      s.height = (h * sy) + 'px';
      s.boxSizing = 'border-box';
      s.zIndex = '1000';
      s.margin = '0';
      s.padding = multiline ? (6 * sy) + 'px ' + (14 * sx) + 'px' : '0 ' + (14 * sx) + 'px';
      s.fontSize = Math.max(10, font * sy) + 'px';
      s.fontFamily = '"M PLUS Rounded 1c", "Hiragino Maru Gothic ProN", sans-serif';
      s.color = colors[0] || '#E6F0E2';
      s.background = colors[1] || '#071419';
      s.border = '2px solid ' + (colors[2] || '#9BE15D');
      s.borderRadius = (10 * sy) + 'px';
      s.outline = 'none';
      s.resize = 'none';
      var created = Date.now(), finished = false;
      var done = function (submit) {
        if (finished) return;
        finished = true;
        window.removeEventListener('resize', onResize);
        send(go, 'OnEnd', id + '\n' + (submit ? 1 : 0) + '\n' + el.value);
        el.remove();
        if (window.twInput && window.twInput.el === el) window.twInput = null;
      };
      var onResize = function () { done(false); };
      el.addEventListener('input', function () { send(go, 'OnText', id + '\n' + el.value); });
      el.addEventListener('blur', function () {
        // 押した直後に Unity の画面がフォーカスを取り返すことがあるので、出した直後の blur は取り消す
        if (Date.now() - created < 400) { setTimeout(function () { if (!finished) el.focus(); }, 0); return; }
        done(false);
      });
      var stop = function (e) { e.stopPropagation(); };
      el.addEventListener('keydown', function (e) {
        e.stopPropagation();
        if (e.isComposing || e.keyCode === 229) return;   // 日本語の変換中は Enter で確定しない
        if (e.key === 'Enter' && !multiline) { e.preventDefault(); done(true); }
        else if (e.key === 'Escape') { e.preventDefault(); done(false); }
      });
      el.addEventListener('keyup', stop);
      el.addEventListener('keypress', stop);
      window.addEventListener('resize', onResize);
      document.body.appendChild(el);
      window.twInput = { el: el, done: done };
      el.focus();
      if (!multiline) el.select();
    };
    document.addEventListener('pointerup', fire, true);
  },

  // 入力中の HTML の入力欄を閉じる（画面を切り替えたときなど）
  TW_CloseInput: function () {
    if (window.twInput) window.twInput.done(false);
  }
});
