// MudBlazor z-index: appbar=1300, drawer=1200, dialog=1400, popover=1500
// Start above appbar/drawer so floating dialogs aren't hidden behind nav
let topZIndex = 1400;

const UNPROCESSED_SELECTOR = ".mud-dialog:not([data-floating-applied])";
const STORAGE_PREFIX = "floating-window:";

function saveWindowState(windowId, state) {
    if (!windowId) {
        return;
    }
    try {
        localStorage.setItem(STORAGE_PREFIX + windowId, JSON.stringify(state));
    } catch {
        // localStorage might be full or disabled
    }
}

function loadWindowState(windowId) {
    if (!windowId) {
        return null;
    }
    try {
        const json = localStorage.getItem(STORAGE_PREFIX + windowId);
        return json ? JSON.parse(json) : null;
    } catch {
        return null;
    }
}

function findNewDialog(expectedTitle) {
    // Find ALL unprocessed dialogs
    const dialogs = document.querySelectorAll(UNPROCESSED_SELECTOR);
    if (dialogs.length === 0) {
        return null;
    }

    // If we have an expected title, find the matching dialog by title
    if (expectedTitle) {
        for (const dialog of dialogs) {
            const titleEl = dialog.querySelector(".mud-dialog-title");
            if (titleEl && titleEl.textContent.trim() === expectedTitle) {
                const container = dialog.closest(".mud-dialog-container");
                if (container) {
                    return { dialog, container };
                }
            }
        }
    }

    // Fallback: take the last one (most recently created)
    const dialog = dialogs[dialogs.length - 1];
    const container = dialog.closest(".mud-dialog-container");
    if (!container) {
        return null;
    }
    return { dialog, container };
}

function findDialogByWindowId(windowId) {
    if (!windowId) {
        return null;
    }
    const dialog = document.querySelector(`.mud-dialog[data-floating-window-id="${windowId}"]`);
    if (!dialog) {
        return null;
    }
    const container = dialog.closest(".mud-dialog-container");
    return container ? { dialog, container } : null;
}

function bringToFront(container) {
    container.style.zIndex = ++topZIndex;
}

function applyFloatingBehavior(dialog, container, options) {
    // Mark as processed with our windowId so we can identify it later
    dialog.setAttribute("data-floating-applied", "true");
    if (options.windowId) {
        dialog.setAttribute("data-floating-window-id", options.windowId);
        container.setAttribute("data-floating-window-id", options.windowId);
    }

    // Hide overlay and make container pass-through
    const overlay = container.querySelector(".mud-overlay-dialog");
    if (overlay) {
        overlay.style.display = "none";
    }
    container.classList.add("floating-dialog-container");

    // Position fixed relative to viewport (not container) to prevent jumping when other dialogs close
    const rect = dialog.getBoundingClientRect();
    dialog.style.position = "fixed";
    dialog.style.margin = "0";
    dialog.style.transform = "none";

    // Try to restore saved state
    const savedState = options.rememberState ? loadWindowState(options.windowId) : null;

    if (savedState) {
        // Apply saved position and size
        dialog.style.left = savedState.x + "px";
        dialog.style.top = savedState.y + "px";
        if (savedState.width) {
            dialog.style.width = savedState.width + "px";
        }
        if (savedState.height) {
            dialog.style.height = savedState.height + "px";
        }
    } else {
        // Use current position
        dialog.style.left = rect.left + "px";
        dialog.style.top = rect.top + "px";
    }

    // Z-index must be on the container (each dialog is in its own full-viewport container)
    bringToFront(container);

    // Click-to-front: raise the container so it stacks above sibling containers
    dialog.addEventListener("pointerdown", () => {
        bringToFront(container);
    });

    if (options.draggable) {
        initDrag(dialog, options);
    }

    if (options.resizable) {
        initResize(dialog, options);
    }
}

export function initFloatingDialog(options) {
    const found = findNewDialog(options.title);
    if (!found) {
        return;
    }
    applyFloatingBehavior(found.dialog, found.container, options);
}

function isClickable(el) {
    if (!el) {
        return false;
    }
    const tag = el.tagName;
    if (tag === "BUTTON" || tag === "A" || tag === "INPUT" || tag === "SELECT" || tag === "TEXTAREA") {
        return true;
    }
    // MudBlazor close button uses nested elements inside <button>
    if (el.closest("button, a, input")) {
        return true;
    }
    return false;
}

function initDrag(dialog, options) {
    const titleBar = dialog.querySelector(".mud-dialog-title");
    if (!titleBar) {
        return;
    }

    titleBar.style.cursor = "move";
    titleBar.style.userSelect = "none";
    titleBar.style.touchAction = "none";

    let isDragging = false;
    let offsetX = 0;
    let offsetY = 0;
    let currentX = 0;
    let currentY = 0;
    let rafId = 0;

    function onPointerDown(e) {
        if (e.button !== 0) {
            return;
        }
        // Don't start drag when clicking buttons (close), links, inputs
        if (isClickable(e.target)) {
            return;
        }
        isDragging = true;
        const dialogRect = dialog.getBoundingClientRect();
        offsetX = e.clientX - dialogRect.left;
        offsetY = e.clientY - dialogRect.top;
        titleBar.setPointerCapture(e.pointerId);
        e.preventDefault();
    }

    function onPointerMove(e) {
        if (!isDragging) {
            return;
        }
        currentX = e.clientX - offsetX;
        currentY = e.clientY - offsetY;

        // Bounds checking
        const maxX = window.innerWidth - 50;
        const maxY = window.innerHeight - 50;
        currentX = Math.max(-dialog.offsetWidth + 50, Math.min(currentX, maxX));
        currentY = Math.max(0, Math.min(currentY, maxY));

        if (!rafId) {
            rafId = requestAnimationFrame(() => {
                dialog.style.left = currentX + "px";
                dialog.style.top = currentY + "px";
                rafId = 0;
            });
        }
    }

    function onPointerUp() {
        isDragging = false;
        if (rafId) {
            cancelAnimationFrame(rafId);
            rafId = 0;
        }
        // Save state on drag end
        if (options.rememberState) {
            saveWindowState(options.windowId, {
                x: parseFloat(dialog.style.left),
                y: parseFloat(dialog.style.top),
                width: dialog.offsetWidth,
                height: dialog.offsetHeight
            });
        }
    }

    titleBar.addEventListener("pointerdown", onPointerDown);
    titleBar.addEventListener("pointermove", onPointerMove);
    titleBar.addEventListener("pointerup", onPointerUp);
}

function initResize(dialog, options) {
    const grip = document.createElement("div");
    grip.className = "floating-dialog-resize-grip";
    dialog.style.overflow = "hidden";
    dialog.appendChild(grip);

    let isResizing = false;
    let startX = 0;
    let startY = 0;
    let startWidth = 0;
    let startHeight = 0;
    let rafId = 0;
    let newWidth = 0;
    let newHeight = 0;

    grip.addEventListener("pointerdown", (e) => {
        if (e.button !== 0) {
            return;
        }
        isResizing = true;
        startX = e.clientX;
        startY = e.clientY;
        startWidth = dialog.offsetWidth;
        startHeight = dialog.offsetHeight;
        grip.setPointerCapture(e.pointerId);
        e.preventDefault();
        e.stopPropagation();
    });

    grip.addEventListener("pointermove", (e) => {
        if (!isResizing) {
            return;
        }
        newWidth = Math.max(200, startWidth + (e.clientX - startX));
        newHeight = Math.max(150, startHeight + (e.clientY - startY));

        if (!rafId) {
            rafId = requestAnimationFrame(() => {
                dialog.style.width = newWidth + "px";
                dialog.style.height = newHeight + "px";
                rafId = 0;
            });
        }
    });

    grip.addEventListener("pointerup", () => {
        isResizing = false;
        if (rafId) {
            cancelAnimationFrame(rafId);
            rafId = 0;
        }
        // Save state on resize end
        if (options.rememberState) {
            saveWindowState(options.windowId, {
                x: parseFloat(dialog.style.left),
                y: parseFloat(dialog.style.top),
                width: dialog.offsetWidth,
                height: dialog.offsetHeight
            });
        }
    });
}
