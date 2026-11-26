import {type KeyboardEvent, type ReactNode, type UIEvent, useLayoutEffect, useRef, useState} from 'react'

type VirtualListProps<T> = {
    items: readonly T[]
    /** Every row is this tall, in pixels. */
    rowHeight: number
    render: (item: T, index: number) => ReactNode
    /** Keeps this row in view, e.g. the keyboard selection. */
    activeIndex?: number
    label: string
    onKeyDown?: (event: KeyboardEvent<HTMLDivElement>) => void
}

const OVERSCAN = 12

/**
 * A scrolling list that only renders the rows in view, so 100,000 changes scroll as smoothly as ten. Rows must all
 * have the same height.
 */
export function VirtualList<T>({items, rowHeight, render, activeIndex, label, onKeyDown}: VirtualListProps<T>) {
    const root = useRef<HTMLDivElement>(null)
    const [scrollTop, setScrollTop] = useState(0)
    const [height, setHeight] = useState(600)

    useLayoutEffect(() => {
        const element = root.current
        if (!element) return
        const observer = new ResizeObserver(() => setHeight(element.clientHeight))
        observer.observe(element)
        setHeight(element.clientHeight)
        return () => observer.disconnect()
    }, [])

    useLayoutEffect(() => {
        const element = root.current
        if (!element || activeIndex === undefined || activeIndex < 0) return
        const top = activeIndex * rowHeight
        if (top < element.scrollTop) element.scrollTop = top
        else if (top + rowHeight > element.scrollTop + element.clientHeight) element.scrollTop = top + rowHeight - element.clientHeight
    }, [activeIndex, rowHeight])

    const first = Math.max(0, Math.floor(scrollTop / rowHeight) - OVERSCAN)
    const last = Math.min(items.length, Math.ceil((scrollTop + height) / rowHeight) + OVERSCAN)
    const rows: ReactNode[] = []
    for (let index = first; index < last; index++) {
        rows.push(
            <div key={index} className="virtual-row" style={{top: index * rowHeight, height: rowHeight}}>
                {render(items[index], index)}
            </div>,
        )
    }

    return (
        <div
            ref={root}
            className="virtual-list"
            role="listbox"
            aria-label={label}
            tabIndex={0}
            onScroll={(event: UIEvent<HTMLDivElement>) => setScrollTop(event.currentTarget.scrollTop)}
            onKeyDown={onKeyDown}
        >
            <div style={{height: items.length * rowHeight, position: 'relative'}}>{rows}</div>
        </div>
    )
}
