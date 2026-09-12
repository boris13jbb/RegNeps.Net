/* Utilidades UI compartidas: modales accesibles (focus-trap + scroll lock). */
window.regnepsUi = (function () {
    const FOCUSABLE =
        'a[href], button:not([disabled]), textarea:not([disabled]), input:not([disabled]):not([type="hidden"]), select:not([disabled]), [tabindex]:not([tabindex="-1"])';

    /** @type {WeakMap<Element, { onKeyDown: EventListener, previouslyFocused: Element | null }>} */
    const state = new WeakMap();

    function getFocusable(root) {
        return Array.from(root.querySelectorAll(FOCUSABLE)).filter(function (el) {
            return !el.hasAttribute('disabled')
                && el.getAttribute('aria-hidden') !== 'true'
                && el.offsetParent !== null;
        });
    }

    function activateModal(root) {
        if (!root || state.has(root)) {
            return;
        }

        document.documentElement.classList.add('rn-modal-open');
        var previouslyFocused = document.activeElement instanceof HTMLElement
            ? document.activeElement
            : null;

        var onKeyDown = function (e) {
            if (e.key !== 'Tab') {
                return;
            }

            var items = getFocusable(root);
            if (items.length === 0) {
                e.preventDefault();
                root.focus();
                return;
            }

            var first = items[0];
            var last = items[items.length - 1];
            if (e.shiftKey && document.activeElement === first) {
                e.preventDefault();
                last.focus();
            } else if (!e.shiftKey && document.activeElement === last) {
                e.preventDefault();
                first.focus();
            }
        };

        root.addEventListener('keydown', onKeyDown);
        state.set(root, { onKeyDown: onKeyDown, previouslyFocused: previouslyFocused });

        var items = getFocusable(root);
        var preferred = items.find(function (el) {
            return !el.classList.contains('btn-close')
                && el.tagName !== 'BUTTON'
                && (el.tagName === 'INPUT' || el.tagName === 'TEXTAREA' || el.tagName === 'SELECT');
        }) || items.find(function (el) {
            return !el.classList.contains('btn-close');
        }) || items[0] || root;

        try {
            preferred.focus();
        } catch (_) {
            /* ignore */
        }
    }

    function deactivateModal(root) {
        document.documentElement.classList.remove('rn-modal-open');
        if (!root) {
            return;
        }

        var entry = state.get(root);
        if (!entry) {
            return;
        }

        root.removeEventListener('keydown', entry.onKeyDown);
        state.delete(root);

        if (entry.previouslyFocused && typeof entry.previouslyFocused.focus === 'function') {
            try {
                entry.previouslyFocused.focus();
            } catch (_) {
                /* ignore */
            }
        }
    }

    return {
        activateModal: activateModal,
        deactivateModal: deactivateModal,
            copyText: async function (text) {
            if (navigator.clipboard && navigator.clipboard.writeText) {
                await navigator.clipboard.writeText(text || '');
                return true;
            }
            var ta = document.createElement('textarea');
            ta.value = text || '';
            ta.setAttribute('readonly', '');
            ta.style.position = 'fixed';
            ta.style.left = '-9999px';
            document.body.appendChild(ta);
            ta.select();
            try {
                document.execCommand('copy');
                return true;
            } finally {
                document.body.removeChild(ta);
            }
        },

        /**
         * Comparte texto. Devuelve un estado explícito (nunca bool):
         * "shared" | "native-requested" | "copied" | "cancelled" | "unsupported" | "failed"
         *
         * "copied" NO es "shared". Cancelar NO dispara clipboard.
         * En la APK MAUI (window.regnepsNativeShareAvailable) usa el puente regneps-share://.
         * Límite de URL del puente: 3500 caracteres (Android WebView).
         */
        shareText: async function (text, title) {
            var payload = text || '';
            var shareTitle = title || 'RegNeps';

            // Puente nativo MAUI/Android: solo si la app lo marcó tras navegación exitosa.
            if (window.regnepsNativeShareAvailable === true) {
                try {
                    var encodedTitle = encodeURIComponent(shareTitle);
                    var encodedText = encodeURIComponent(payload);
                    var nativeUrl = 'regneps-share://share?title=' + encodedTitle + '&text=' + encodedText;
                    // Límite práctico de URL en WebView Android; no crashear si se excede.
                    if (nativeUrl.length > 3500) {
                        try {
                            var copiedLong = await window.regnepsUi.copyText(payload);
                            return copiedLong ? 'copied' : 'unsupported';
                        } catch (_) {
                            return 'unsupported';
                        }
                    }

                    var iframe = document.createElement('iframe');
                    iframe.setAttribute('aria-hidden', 'true');
                    iframe.style.cssText = 'display:none;width:0;height:0;border:0;position:absolute';
                    iframe.src = nativeUrl;
                    document.body.appendChild(iframe);
                    setTimeout(function () {
                        try {
                            document.body.removeChild(iframe);
                        } catch (_) {
                            /* ignore */
                        }
                    }, 1500);
                    return 'native-requested';
                } catch (_) {
                    return 'failed';
                }
            }

            if (typeof navigator.share === 'function') {
                try {
                    await navigator.share({
                        title: shareTitle,
                        text: payload
                    });
                    return 'shared';
                } catch (err) {
                    if (err && (err.name === 'AbortError' || err.name === 'NotAllowedError')) {
                        // Cancelación del usuario: no copiar al portapapeles.
                        return 'cancelled';
                    }
                    // Fallo distinto de cancelación → intentar clipboard.
                }
            }

            try {
                var copied = await window.regnepsUi.copyText(payload);
                return copied ? 'copied' : 'unsupported';
            } catch (_) {
                return 'failed';
            }
        },
        scrollIntoView: function (el, options) {
            if (!el || typeof el.scrollIntoView !== 'function') {
                return;
            }
            el.scrollIntoView(options || { behavior: 'smooth', block: 'start' });
        },

        scrollToId: function (id) {
            if (!id) {
                return;
            }
            var el = document.getElementById(id);
            if (!el) {
                return;
            }
            el.scrollIntoView({ behavior: 'smooth', block: 'start' });
            try {
                el.focus({ preventScroll: true });
            } catch (_) {
                /* ignore */
            }
        },

        setBodyScrollLocked: function (locked) {
            document.documentElement.classList.toggle('rn-modal-open', !!locked);
        },

        /** IDs de alertas marcadas como leídas en el centro de notificaciones (UI). */
        getNotificationSeenIds: function () {
            try {
                var raw = localStorage.getItem('regneps.notif.seen');
                if (!raw) {
                    return [];
                }
                var parsed = JSON.parse(raw);
                return Array.isArray(parsed) ? parsed.filter(function (x) { return typeof x === 'string'; }) : [];
            } catch (_) {
                return [];
            }
        },

        setNotificationSeenIds: function (ids) {
            try {
                var list = Array.isArray(ids) ? ids : [];
                localStorage.setItem('regneps.notif.seen', JSON.stringify(list));
            } catch (_) {
                /* storage lleno o modo privado */
            }
        },

        /** Sesión de captura activa por usuario (recuperación al volver a /captura). */
        getCaptureSessionId: function (userId) {
            if (!userId) {
                return null;
            }
            try {
                return sessionStorage.getItem('regneps.captureSession.' + userId);
            } catch (_) {
                return null;
            }
        },

        setCaptureSessionId: function (userId, sessionId) {
            if (!userId) {
                return;
            }
            try {
                var key = 'regneps.captureSession.' + userId;
                if (!sessionId) {
                    sessionStorage.removeItem(key);
                    return;
                }
                sessionStorage.setItem(key, sessionId);
            } catch (_) {
                /* storage no disponible */
            }
        }
    };
})();
