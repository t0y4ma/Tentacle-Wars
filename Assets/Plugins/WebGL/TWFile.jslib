// ブラウザでのファイル読み込み・書き出し・コピー（Tentacle Wars）
mergeInto(LibraryManager.library, {
  // ボタンを押し下げた時に呼ばれ、指を離した瞬間（ユーザー操作の中）にファイル選択を開く
  TW_ArmOpenFile: function (goPtr, methodPtr, acceptPtr) {
    var go = UTF8ToString(goPtr), method = UTF8ToString(methodPtr), accept = UTF8ToString(acceptPtr);
    var send = (typeof SendMessage !== 'undefined') ? SendMessage : Module.SendMessage;
    var fire = function () {
      document.removeEventListener('pointerup', fire, true);
      var input = document.createElement('input');
      input.type = 'file';
      input.accept = accept;
      input.style.display = 'none';
      input.onchange = function () {
        var f = input.files && input.files[0];
        if (f) {
          var r = new FileReader();
          r.onload = function () { send(go, method, String(r.result)); };
          r.readAsText(f);
        }
        input.remove();
      };
      document.body.appendChild(input);
      input.click();
    };
    document.addEventListener('pointerup', fire, true);
  },

  TW_DownloadText: function (namePtr, textPtr) {
    var name = UTF8ToString(namePtr), text = UTF8ToString(textPtr);
    var blob = new Blob([text], { type: 'application/json' });
    var a = document.createElement('a');
    a.href = URL.createObjectURL(blob);
    a.download = name;
    document.body.appendChild(a);
    a.click();
    setTimeout(function () { URL.revokeObjectURL(a.href); a.remove(); }, 1000);
  },

  TW_ArmCopyText: function (textPtr) {
    var text = UTF8ToString(textPtr);
    var fire = function () {
      document.removeEventListener('pointerup', fire, true);
      if (navigator.clipboard) navigator.clipboard.writeText(text);
    };
    document.addEventListener('pointerup', fire, true);
  }
});
