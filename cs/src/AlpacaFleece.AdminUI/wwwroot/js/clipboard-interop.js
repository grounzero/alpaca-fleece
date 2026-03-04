// Clipboard interop — wraps navigator.clipboard.writeText
window.clipboardInterop = {
    copyText: async (text) => {
        try {
            await navigator.clipboard.writeText(text);
            return true;
        } catch {
            // Fallback for older browsers / non-secure contexts
            const el = document.createElement('textarea');
            el.value = text;
            el.style.position = 'fixed';
            el.style.opacity = '0';
            document.body.appendChild(el);
            el.focus();
            el.select();
            const ok = document.execCommand('copy');
            document.body.removeChild(el);
            return ok;
        }
    }
};
