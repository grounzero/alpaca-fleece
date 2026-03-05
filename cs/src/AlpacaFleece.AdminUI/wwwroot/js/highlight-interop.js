window.highlightInterop = {
    highlight: (elementId) => {
        const el = document.getElementById(elementId);
        if (el && window.hljs) window.hljs.highlightElement(el);
    }
};
