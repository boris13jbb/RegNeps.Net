/**
 * Bridge WebView ↔ MAUI (Fase 2D.1).
 * Solo acciones explícitas; sin execute(any). Sin secretos en el payload.
 */
window.regnepsOfflineBridge = (function () {
    var PROTOCOL_VERSION = 1;
    var SCHEME = 'regneps-bridge';
    var pending = {};
    var readyWaiters = [];

    function isNativeAvailable() {
        return window.regnepsOfflineBridgeAvailable === true;
    }

    function _markReady() {
        window.regnepsOfflineBridgeAvailable = true;
        var waiters = readyWaiters.slice();
        readyWaiters.length = 0;
        waiters.forEach(function (fn) {
            try { fn(); } catch (_) { /* ignore */ }
        });
    }

    function deliver(jsonStr) {
        var envelope;
        try {
            envelope = typeof jsonStr === 'string' ? JSON.parse(jsonStr) : jsonStr;
        } catch (_) {
            return;
        }

        if (!envelope || !envelope.requestId) {
            return;
        }

        var entry = pending[envelope.requestId];
        if (!entry) {
            return;
        }

        delete pending[envelope.requestId];
        clearTimeout(entry.timer);
        if (envelope.ok) {
            entry.resolve(envelope);
        } else {
            var err = new Error(envelope.errorMessage || envelope.errorCode || 'bridge_error');
            err.code = envelope.errorCode;
            err.envelope = envelope;
            entry.reject(err);
        }
    }

    function buildUrl(envelope) {
        var json = JSON.stringify(envelope);
        return SCHEME + '://invoke?data=' + encodeURIComponent(json);
    }

    /**
     * @param {string} action
     * @param {object|null} payload
     * @param {number} [timeoutMs]
     * @returns {Promise<object>}
     */
    function request(action, payload, timeoutMs) {
        timeoutMs = typeof timeoutMs === 'number' ? timeoutMs : 8000;

        return new Promise(function (resolve, reject) {
            if (!isNativeAvailable()) {
                var na = new Error('Bridge nativo no disponible');
                na.code = 'NOT_AVAILABLE';
                reject(na);
                return;
            }

            var requestId = (window.crypto && crypto.randomUUID)
                ? crypto.randomUUID().replace(/-/g, '')
                : (Date.now().toString(16) + Math.random().toString(16).slice(2));

            var envelope = {
                protocolVersion: PROTOCOL_VERSION,
                messageType: 'request',
                requestId: requestId,
                action: action,
                payload: payload == null ? {} : payload
            };

            var timer = setTimeout(function () {
                delete pending[requestId];
                var te = new Error('Timeout del bridge');
                te.code = 'TIMEOUT';
                reject(te);
            }, timeoutMs);

            pending[requestId] = { resolve: resolve, reject: reject, timer: timer };

            try {
                // Navegación interceptada por MAUI (OnBrowserNavigating).
                window.location.href = buildUrl(envelope);
            } catch (ex) {
                clearTimeout(timer);
                delete pending[requestId];
                reject(ex);
            }
        });
    }

    function saveSession(snapshot) {
        return request('offline.session.save', snapshot || {});
    }

    function clearSession() {
        return request('offline.session.clear', {});
    }

    function getSession() {
        return request('offline.session.get', {});
    }

    function openCapture() {
        return request('offline.capture.open', {});
    }

    /**
     * Limpia LocalSession (si hay bridge) y envía POST /api/logout.
     * No borra Outbox en nativo; solo el snapshot UX.
     */
    function clearSessionAndLogout() {
        var finish = function () {
            var form = document.createElement('form');
            form.method = 'post';
            form.action = '/api/logout';
            form.style.display = 'none';
            document.body.appendChild(form);
            form.submit();
        };

        if (!isNativeAvailable()) {
            finish();
            return Promise.resolve();
        }

        return clearSession().catch(function () {
            /* logout servidor igual debe ocurrir */
        }).then(finish);
    }

    return {
        protocolVersion: PROTOCOL_VERSION,
        request: request,
        saveSession: saveSession,
        clearSession: clearSession,
        getSession: getSession,
        openCapture: openCapture,
        clearSessionAndLogout: clearSessionAndLogout,
        deliver: deliver,
        _markReady: _markReady,
        isNativeAvailable: isNativeAvailable
    };
})();
