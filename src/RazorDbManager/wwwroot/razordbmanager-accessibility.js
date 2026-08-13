export function focusElementById(id) {
    document.getElementById(id)?.focus();
}

export function activateModal(root) {
    const previous = document.activeElement instanceof HTMLElement ? document.activeElement : null;
    const selector = "button:not([disabled]),[href],input:not([disabled]),select:not([disabled]),textarea:not([disabled]),[tabindex]:not([tabindex='-1'])";
    const focusables = () => Array.from(root.querySelectorAll(selector))
        .filter(element => element instanceof HTMLElement && element.offsetParent !== null);
    const onKeyDown = event => {
        if (event.key === "Escape") {
            event.preventDefault();
            const close = root.querySelector("[data-rdm-modal-close]");
            if (close instanceof HTMLElement && !close.hasAttribute("disabled")) close.click();
            return;
        }
        if (event.key !== "Tab") return;
        const items = focusables();
        if (items.length === 0) { event.preventDefault(); root.focus(); return; }
        const first = items[0], last = items[items.length - 1];
        if (event.shiftKey && document.activeElement === first) { event.preventDefault(); last.focus(); }
        else if (!event.shiftKey && document.activeElement === last) { event.preventDefault(); first.focus(); }
    };
    root.addEventListener("keydown", onKeyDown);
    queueMicrotask(() => {
        const target = root.querySelector("[data-rdm-modal-close]") ?? focusables()[0];
        if (target instanceof HTMLElement) target.focus();
    });
    return { deactivate() { root.removeEventListener("keydown", onKeyDown); if (previous?.isConnected) previous.focus(); } };
}
