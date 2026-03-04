// localStorage interop — used for config editor draft persistence
window.localStorageInterop = {
    get: (key) => {
        try { return localStorage.getItem(key); } catch { return null; }
    },
    set: (key, value) => {
        try { localStorage.setItem(key, value); } catch {}
    },
    remove: (key) => {
        try { localStorage.removeItem(key); } catch {}
    }
};
