import {type ReactNode, useCallback, useRef, useState} from 'react'

function rememberedWidth(key: string, fallback: number): number {
    try {
        const stored = Number(localStorage.getItem(key))
        return Number.isFinite(stored) && stored > 0 ? stored : fallback
    } catch {
        return fallback
    }
}

type SplitPaneProps = {
    /** Remembers the divider position per page, e.g. `changes`. */
    id: string
    list: ReactNode
    detail: ReactNode
    initial?: number
    min?: number
    max?: number
}

/**
 * The list/detail layout every page uses: a resizable list on the left and the selected item's details on the right.
 * Drag the divider, or focus it and use the arrow keys.
 */
export function SplitPane({id, list, detail, initial = 380, min = 260, max = 720}: SplitPaneProps) {
    const key = `vertex.split.${id}`
    const [width, setWidth] = useState(() => rememberedWidth(key, initial))
    const dragging = useRef(false)

    const commit = useCallback((next: number) => {
        const clamped = Math.round(Math.min(max, Math.max(min, next)))
        setWidth(clamped)
        try {
            localStorage.setItem(key, String(clamped))
        } catch {
            // Only the position is lost.
        }
    }, [key, min, max])

    return (
        <div className="split-pane" style={{gridTemplateColumns: `${width}px 1px minmax(0, 1fr)`}}>
            <section className="split-list">{list}</section>
            <div
                className="split-divider"
                role="separator"
                aria-orientation="vertical"
                aria-valuenow={width}
                aria-valuemin={min}
                aria-valuemax={max}
                tabIndex={0}
                onPointerDown={event => {
                    dragging.current = true
                    const startX = event.clientX
                    const startWidth = width
                    const target = event.currentTarget
                    target.setPointerCapture(event.pointerId)
                    const move = (e: PointerEvent) => {
                        if (dragging.current) commit(startWidth + e.clientX - startX)
                    }
                    const up = () => {
                        dragging.current = false
                        target.removeEventListener('pointermove', move)
                        target.removeEventListener('pointerup', up)
                    }
                    target.addEventListener('pointermove', move)
                    target.addEventListener('pointerup', up)
                }}
                onKeyDown={event => {
                    if (event.key === 'ArrowLeft') commit(width - 24)
                    if (event.key === 'ArrowRight') commit(width + 24)
                }}
            />
            <section className="split-detail">{detail}</section>
        </div>
    )
}
