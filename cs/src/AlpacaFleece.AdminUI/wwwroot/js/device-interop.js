// Device detection interop — used by InfoPopup to choose tooltip vs dialog
window.deviceInterop = {
    isMobile: () => window.innerWidth < 960,
    getWindowWidth: () => window.innerWidth
};
