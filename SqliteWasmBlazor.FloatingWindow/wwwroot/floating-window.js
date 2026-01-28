// FloatingWindow JavaScript Module
// Handles pointer capture for smooth drag and resize operations

// Disable text selection globally during drag/resize
function startDragging() {
    document.body.classList.add('fw-dragging');
}

function stopDragging() {
    document.body.classList.remove('fw-dragging');
}

function startResizing() {
    document.body.classList.add('fw-resizing');
}

function stopResizing() {
    document.body.classList.remove('fw-resizing');
}

// Snap constants
const SNAP_THRESHOLD = 20; // pixels from edge to trigger snap

// Shared snap preview element
let snapPreviewElement = null;
let currentSnapZone = null;

/**
 * Gets the snap zone based on cursor position.
 * @param {number} clientX - Cursor X position
 * @param {number} clientY - Cursor Y position
 * @returns {string|null} - 'left', 'right', 'top', 'bottom', or null
 */
function getSnapZone(clientX, clientY) {
    const vw = window.innerWidth;
    const vh = window.innerHeight;

    if (clientX <= SNAP_THRESHOLD) {
        return 'left';
    }
    if (clientX >= vw - SNAP_THRESHOLD) {
        return 'right';
    }
    if (clientY <= SNAP_THRESHOLD) {
        return 'top';
    }
    if (clientY >= vh - SNAP_THRESHOLD) {
        return 'bottom';
    }
    return null;
}

/**
 * Shows the snap preview overlay for the given zone.
 * @param {string} zone - 'left', 'right', 'top', or 'bottom'
 */
function showSnapPreview(zone) {
    if (currentSnapZone === zone) {
        return;
    }

    if (snapPreviewElement === null) {
        snapPreviewElement = document.createElement('div');
        snapPreviewElement.className = 'fw-snap-preview';
        document.body.appendChild(snapPreviewElement);
    }

    // Remove old zone class
    if (currentSnapZone !== null) {
        snapPreviewElement.classList.remove(`fw-snap-${currentSnapZone}`);
    }

    // Add new zone class
    snapPreviewElement.classList.add(`fw-snap-${zone}`);
    currentSnapZone = zone;
}

/**
 * Hides the snap preview overlay.
 */
function hideSnapPreview() {
    if (snapPreviewElement !== null) {
        snapPreviewElement.remove();
        snapPreviewElement = null;
    }
    currentSnapZone = null;
}

/**
 * Initializes drag behavior on a window header.
 * @param {string} elementId - The window element ID (fw-{id})
 * @param {DotNetObjectReference} dotNetRef - Reference to the Blazor component
 * @param {boolean} canSnap - Whether snap-to-edge is enabled
 * @param {boolean} isSnapped - Whether the window is currently snapped
 * @param {number|null} preSnapWidth - Width before snap (for restore)
 * @param {number|null} preSnapHeight - Height before snap (for restore)
 */
export function initDrag(elementId, dotNetRef, canSnap = true, isSnapped = false, preSnapWidth = null, preSnapHeight = null) {
    const win = document.getElementById(elementId);
    if (!win) {
        return;
    }

    const header = win.querySelector('.fw-header');
    if (!header) {
        return;
    }

    let isDragging = false;
    let offsetX = 0;
    let offsetY = 0;
    let finalX = 0;
    let finalY = 0;
    let snapEnabled = canSnap;
    let wasSnapped = isSnapped;
    let restoreWidth = preSnapWidth;
    let restoreHeight = preSnapHeight;
    let hasRestoredFromSnap = false;
    let hasMoved = false;
    let startX = 0;
    let startY = 0;
    const MOVE_THRESHOLD = 5; // pixels - minimum movement to count as a drag

    function onPointerDown(e) {
        if (e.button !== 0) {
            return;
        }

        if (e.target.closest('button, a, input, .mud-icon-button')) {
            return;
        }

        isDragging = true;
        hasRestoredFromSnap = false;
        hasMoved = false;
        startX = e.clientX;
        startY = e.clientY;

        const rect = win.getBoundingClientRect();
        offsetX = e.clientX - rect.left;
        offsetY = e.clientY - rect.top;

        // If window is snapped and we have pre-snap dimensions, prepare for restore
        if (wasSnapped && restoreWidth !== null && restoreHeight !== null) {
            // Calculate new offset to keep cursor at relative position within restored window
            const relativeX = offsetX / rect.width;
            const relativeY = offsetY / rect.height;
            offsetX = relativeX * restoreWidth;
            offsetY = relativeY * restoreHeight;
        }

        header.setPointerCapture(e.pointerId);
        startDragging();
        e.preventDefault();
    }

    function onPointerMove(e) {
        if (!isDragging) {
            return;
        }

        // Check if we've moved beyond the threshold to count as an actual drag
        if (!hasMoved) {
            const deltaX = Math.abs(e.clientX - startX);
            const deltaY = Math.abs(e.clientY - startY);
            if (deltaX < MOVE_THRESHOLD && deltaY < MOVE_THRESHOLD) {
                return; // Not moved enough yet
            }
            hasMoved = true;
        }

        // If snapped and moving, restore to pre-snap size
        if (wasSnapped && !hasRestoredFromSnap && restoreWidth !== null && restoreHeight !== null) {
            win.style.width = restoreWidth + 'px';
            win.style.height = restoreHeight + 'px';
            hasRestoredFromSnap = true;
            // Notify Blazor about restore from snap
            dotNetRef.invokeMethodAsync('OnRestoreFromSnap');
        }

        // Calculate new position
        let newX = e.clientX - offsetX;
        let newY = e.clientY - offsetY;

        // Bounds: keep at least 50px visible, don't go above viewport
        const maxX = window.innerWidth - 50;
        const maxY = window.innerHeight - 50;
        newX = Math.max(-win.offsetWidth + 50, Math.min(newX, maxX));
        newY = Math.max(0, Math.min(newY, maxY));

        // Update DOM directly - no Blazor interop during drag
        win.style.left = newX + 'px';
        win.style.top = newY + 'px';

        finalX = newX;
        finalY = newY;

        // Check for snap zone if snapping is enabled
        if (snapEnabled) {
            const zone = getSnapZone(e.clientX, e.clientY);
            if (zone !== null) {
                showSnapPreview(zone);
            } else {
                hideSnapPreview();
            }
        }
    }

    function onPointerUp(e) {
        if (!isDragging) {
            return;
        }

        isDragging = false;
        header.releasePointerCapture(e.pointerId);
        stopDragging();
        hideSnapPreview();

        // Only process drag end if we actually moved
        if (!hasMoved) {
            return;
        }

        // Check if we should snap
        if (snapEnabled) {
            const zone = getSnapZone(e.clientX, e.clientY);

            if (zone !== null) {
                // Notify Blazor about snap
                dotNetRef.invokeMethodAsync('OnSnapToEdge', zone);
                return;
            }
        }

        // Sync final position to Blazor state
        dotNetRef.invokeMethodAsync('OnDragEnd', finalX, finalY);
    }

    header.addEventListener('pointerdown', onPointerDown);
    header.addEventListener('pointermove', onPointerMove);
    header.addEventListener('pointerup', onPointerUp);
    header.addEventListener('pointercancel', onPointerUp);

    // Store state update function for later updates
    win._fwUpdateSnapState = (newCanSnap, newIsSnapped, newPreSnapWidth, newPreSnapHeight) => {
        snapEnabled = newCanSnap;
        wasSnapped = newIsSnapped;
        restoreWidth = newPreSnapWidth;
        restoreHeight = newPreSnapHeight;
    };

    win._fwDragCleanup = () => {
        header.removeEventListener('pointerdown', onPointerDown);
        header.removeEventListener('pointermove', onPointerMove);
        header.removeEventListener('pointerup', onPointerUp);
        header.removeEventListener('pointercancel', onPointerUp);
        delete win._fwUpdateSnapState;
    };
}

/**
 * Updates the snap state for a window.
 * @param {string} elementId - The window element ID (fw-{id})
 * @param {boolean} canSnap - Whether snap-to-edge is enabled
 * @param {boolean} isSnapped - Whether the window is currently snapped
 * @param {number|null} preSnapWidth - Width before snap (for restore)
 * @param {number|null} preSnapHeight - Height before snap (for restore)
 */
export function updateSnapState(elementId, canSnap, isSnapped, preSnapWidth, preSnapHeight) {
    const win = document.getElementById(elementId);
    if (win && win._fwUpdateSnapState) {
        win._fwUpdateSnapState(canSnap, isSnapped, preSnapWidth, preSnapHeight);
    }
}

/**
 * Initializes resize behavior on window edges/corners.
 * @param {string} elementId - The window element ID (fw-{id})
 * @param {DotNetObjectReference} dotNetRef - Reference to the Blazor component
 */
export function initResize(elementId, dotNetRef) {
    const win = document.getElementById(elementId);
    if (!win) {
        return;
    }

    const handles = win.querySelectorAll('.fw-resize');
    if (handles.length === 0) {
        return;
    }

    const cleanupFns = [];

    handles.forEach(handle => {
        let isResizing = false;
        let startMouseX = 0;
        let startMouseY = 0;
        let startLeft = 0;
        let startTop = 0;
        let startWidth = 0;
        let startHeight = 0;
        let finalX = 0;
        let finalY = 0;
        let finalW = 0;
        let finalH = 0;

        const edge = handle.className.match(/fw-resize-(\w+)/)?.[1] || 'se';

        function onPointerDown(e) {
            if (e.button !== 0) {
                return;
            }

            isResizing = true;
            startMouseX = e.clientX;
            startMouseY = e.clientY;

            const rect = win.getBoundingClientRect();
            startLeft = rect.left;
            startTop = rect.top;
            startWidth = rect.width;
            startHeight = rect.height;

            handle.setPointerCapture(e.pointerId);
            startResizing();
            e.preventDefault();
            e.stopPropagation();
        }

        function onPointerMove(e) {
            if (!isResizing) {
                return;
            }

            const deltaX = e.clientX - startMouseX;
            const deltaY = e.clientY - startMouseY;

            let newX = startLeft;
            let newY = startTop;
            let newW = startWidth;
            let newH = startHeight;

            const minW = parseInt(win.style.minWidth) || 200;
            const minH = parseInt(win.style.minHeight) || 100;

            if (edge.includes('e')) {
                newW = Math.max(minW, startWidth + deltaX);
            }
            if (edge.includes('w')) {
                const proposedW = startWidth - deltaX;
                if (proposedW >= minW) {
                    newW = proposedW;
                    newX = startLeft + deltaX;
                }
            }
            if (edge.includes('s')) {
                newH = Math.max(minH, startHeight + deltaY);
            }
            if (edge.includes('n')) {
                const proposedH = startHeight - deltaY;
                if (proposedH >= minH) {
                    newH = proposedH;
                    newY = startTop + deltaY;
                }
            }

            // Update DOM directly - no Blazor interop during resize
            win.style.left = newX + 'px';
            win.style.top = newY + 'px';
            win.style.width = newW + 'px';
            win.style.height = newH + 'px';

            finalX = newX;
            finalY = newY;
            finalW = newW;
            finalH = newH;
        }

        function onPointerUp(e) {
            if (!isResizing) {
                return;
            }

            isResizing = false;
            handle.releasePointerCapture(e.pointerId);
            stopResizing();

            // Sync final geometry to Blazor state
            dotNetRef.invokeMethodAsync('OnResizeEnd', finalX, finalY, finalW, finalH);
        }

        handle.addEventListener('pointerdown', onPointerDown);
        handle.addEventListener('pointermove', onPointerMove);
        handle.addEventListener('pointerup', onPointerUp);
        handle.addEventListener('pointercancel', onPointerUp);

        cleanupFns.push(() => {
            handle.removeEventListener('pointerdown', onPointerDown);
            handle.removeEventListener('pointermove', onPointerMove);
            handle.removeEventListener('pointerup', onPointerUp);
            handle.removeEventListener('pointercancel', onPointerUp);
        });
    });

    win._fwResizeCleanup = () => {
        cleanupFns.forEach(fn => fn());
    };
}

/**
 * Cleans up all event listeners for a window.
 * @param {string} elementId - The window element ID (fw-{id})
 */
export function dispose(elementId) {
    const window = document.getElementById(elementId);
    if (!window) {
        return;
    }

    if (window._fwDragCleanup) {
        window._fwDragCleanup();
        delete window._fwDragCleanup;
    }

    if (window._fwResizeCleanup) {
        window._fwResizeCleanup();
        delete window._fwResizeCleanup;
    }
}

