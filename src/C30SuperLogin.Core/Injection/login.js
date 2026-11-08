(function () {
    'use strict';
    if (window.C30SuperLogin && window.C30SuperLogin.version === '0.1.0-accounts-v5') return;
    const recent = new Set();
    window.C30SuperLogin = {
        version: '0.1.0-accounts-v5',
        run: function (options) {
            const receipt = function (event) {
                // Only an ephemeral loopback receipt; never include account data or native page content.
                if (typeof options.receipt === 'string' && /^http:\/\/127\.0\.0\.1:\d+\/receipt\/[a-f0-9]{32}$/.test(options.receipt)) {
                    const beacon = new Image();
                    beacon.src = options.receipt + '?event=' + encodeURIComponent(event);
                }
                return event;
            };
            const activationReceipt = function (event, value) {
                if (typeof options.receipt !== 'string' || !/^http:\/\/127\.0\.0\.1:\d+\/receipt\/[a-f0-9]{32}$/.test(options.receipt)) return 'invalid_receipt';
                try {
                    const request = new XMLHttpRequest();
                    request.open('POST', options.receipt + '?event=' + encodeURIComponent(event), true);
                    request.setRequestHeader('Content-Type', 'application/json;charset=UTF-8');
                    request.send(JSON.stringify(value));
                    return event;
                } catch (_) { return receipt('bridge_error'); }
            };
            if (options.operation === 'activation_status' || options.operation === 'activation_open') {
                if (typeof wincore === 'undefined' || !wincore.System)
                    return receipt('activation_not_ready');
                if (options.operation === 'activation_status') {
                    if (typeof wincore.System.get_stupc_register !== 'function')
                        return receipt('activation_not_ready');
                    try {
                        const value = wincore.System.get_stupc_register();
                        const authorized = value && (value.result === true || value.result === 'true' || value.result === 1 || value.result === '1');
                        return activationReceipt('activation_status', {
                            authorized: !!authorized,
                            time: value && typeof value.time === 'string' ? value.time.slice(0, 128) : '',
                            tips: value && typeof value.tips === 'string' ? value.tips.slice(0, 512) : ''
                        });
                    } catch (_) { return receipt('bridge_error'); }
                }
                if (typeof wincore.System.show_register !== 'function')
                    return receipt('activation_not_ready');
                try {
                    wincore.System.show_register(1);
                    return receipt('activation_requested');
                } catch (_) { return receipt('bridge_error'); }
            }
            if (!/\/signin\/index\.html$/i.test(window.location.pathname || '')) return receipt('page_mismatch');
            const username = document.getElementById('loginname');
            const password = document.getElementById('loginpwd');
            const button = document.getElementById('loginbtn');
            if (!username || !password || !button || typeof cef === 'undefined' || !cef.message ||
                typeof cef.message.sendMessage !== 'function' || typeof vm === 'undefined' || !vm.base64_Encode) {
                return receipt('not_ready');
            }
            if (options.operation === 'probe') return receipt('ready');
            if (options.operation === 'list_saved') {
                if (typeof userArry === 'undefined' || !userArry.loading || !Array.isArray(userArry.list)) return receipt('not_ready');
                // Explicit local-only metadata projection. No pwd/token/avatar URL leaves C30.
                const accounts = userArry.list.slice(0, 256).filter(function (entry) {
                    return entry && typeof entry.uname === 'string' && entry.uname.length > 0 && entry.uname.length <= 256;
                }).map(function (entry) {
                    return { uid: String(entry.uid || '').slice(0, 128), username: entry.uname,
                        displayName: String(entry.nickname || entry.uname).slice(0, 128),
                        hasSavedPassword: String(entry.isrecord) === 'true' && typeof entry.pwd === 'string' && entry.pwd.length > 0 };
                });
                if (typeof options.receipt !== 'string' || !/^http:\/\/127\.0\.0\.1:\d+\/receipt\/[a-f0-9]{32}$/.test(options.receipt)) return 'invalid_receipt';
                try {
                    const request = new XMLHttpRequest();
                    request.open('POST', options.receipt + '?event=accounts', true);
                    request.setRequestHeader('Content-Type', 'text/plain;charset=UTF-8');
                    request.send(JSON.stringify(accounts)); return 'accounts';
                } catch (_) { return receipt('bridge_error'); }
            }
            if (options.operation !== 'login' && options.operation !== 'login_saved') return receipt('invalid_operation');
            if (!options.requestId || recent.has(options.requestId)) return receipt('duplicate');
            if (button.disabled) return receipt('busy');
            let loginUsername = options.username, loginPassword = options.password;
            const saved = options.operation === 'login_saved';
            if (saved) {
                if (typeof userArry === 'undefined' || !userArry.loading || !Array.isArray(userArry.list)) return receipt('not_ready');
                const matches = userArry.list.filter(function (entry) {
                    return entry.uname === options.username && (!options.savedUid || String(entry.uid) === options.savedUid);
                });
                if (matches.length !== 1) return receipt(matches.length ? 'saved_ambiguous' : 'saved_missing');
                if (String(matches[0].isrecord) !== 'true' || !matches[0].pwd) return receipt('password_required');
                if (typeof vm.base64_Decode !== 'function') return receipt('not_ready');
                try { loginPassword = vm.base64_Decode(matches[0].pwd); } catch (_) { return receipt('bridge_error'); }
            }
            if (typeof loginUsername !== 'string' || !loginUsername || loginUsername.length > 256 ||
                typeof loginPassword !== 'string' || !loginPassword || loginPassword.length > 4096) return receipt('invalid_credentials');
            try {
                username.value = loginUsername;
                username.dispatchEvent(new Event('input', { bubbles: true }));
                username.dispatchEvent(new Event('keyup', { bubbles: true }));
                password.value = loginPassword;
                password.dispatchEvent(new Event('input', { bubbles: true }));
                const data = { username: loginUsername, password: vm.base64_Encode(loginPassword),
                    isrecord: saved ? 'true' : 'false', logintype: saved ? 'autologin' : 'account' };
                // Match the original saved-account autologin encryption contract.
                if (saved) { data.issetclass = '0'; data.needEncrypt = 'true'; }
                const payload = JSON.stringify(data);
                recent.add(options.requestId);
                if (recent.size > 8) recent.delete(recent.values().next().value);
                cef.message.sendMessage('mirco.call_cplus', ['', 'closekeyboard']);
                cef.message.sendMessage('mirco.login', [payload, 'login']);
                localStorage.removeItem('currentClass');
                button.disabled = true;
                if (typeof $ === 'function') $('#loginbtn').html('登录中...');
                // This acknowledges submission only. It does not assert authentication success.
                return receipt('submitted');
            } catch (_) { return receipt('bridge_error'); }
        }
    };
}());
