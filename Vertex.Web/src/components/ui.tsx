import {type ReactNode, useEffect, useRef} from 'react'
import {Icon, type IconName} from './Icon'

/** A compact header at the top of a pane: title, a count, and actions on the right. */
export function PaneHeader({title, count, children}: {title: string; count?: number | string; children?: ReactNode}) {
    return (
        <header className="pane-header">
            <h2>
                {title}
                {count !== undefined && <span className="count">{typeof count === 'number' ? count.toLocaleString() : count}</span>}
            </h2>
            <div className="pane-actions">{children}</div>
        </header>
    )
}

/** A keyboard key, e.g. <Kbd>Ctrl</Kbd>. */
export function Kbd({children}: {children: ReactNode}) {
    return <kbd className="kbd">{children}</kbd>
}

/** A bar for a running phase; indeterminate while `total` is unknown. */
export function ProgressBar({done, total}: {done: number; total: number}) {
    const fraction = total > 0 ? Math.min(1, done / total) : undefined
    return (
        <span className={`progress-bar ${fraction === undefined ? 'indeterminate' : ''}`} role="progressbar" aria-valuenow={fraction === undefined ? undefined : Math.round(fraction * 100)}>
            <i style={fraction === undefined ? undefined : {width: `${fraction * 100}%`}}/>
        </span>
    )
}

type BannerTone = 'info' | 'warning' | 'error' | 'success'

/** A message across the top of a pane, with an optional fix as a button. */
export function Banner({tone, icon, children, action, onDismiss}: {
    tone: BannerTone
    icon?: IconName
    children: ReactNode
    action?: {label: string; onClick: () => void}
    onDismiss?: () => void
}) {
    return (
        <div className={`banner ${tone}`} role={tone === 'error' ? 'alert' : 'status'}>
            <Icon name={icon ?? (tone === 'success' ? 'check' : tone === 'info' ? 'bell' : 'alert')} size={15}/>
            <div className="banner-text">{children}</div>
            {action && (
                <button type="button" className="button small" onClick={action.onClick}>
                    {action.label}
                </button>
            )}
            {onDismiss && (
                <button type="button" className="icon-button" aria-label="Dismiss" onClick={onDismiss}>
                    <Icon name="close" size={14}/>
                </button>
            )}
        </div>
    )
}

/** A dialog over the app. Escape or a click outside closes it. */
export function Modal({title, children, onClose, footer}: {title: string; children: ReactNode; onClose: () => void; footer?: ReactNode}) {
    useEffect(() => {
        const close = (event: KeyboardEvent) => {
            if (event.key === 'Escape') onClose()
        }
        document.addEventListener('keydown', close)
        return () => document.removeEventListener('keydown', close)
    }, [onClose])

    return (
        <div className="modal-backdrop" onMouseDown={event => event.target === event.currentTarget && onClose()}>
            <div className="modal" role="dialog" aria-modal="true" aria-label={title}>
                <header>
                    <h2>{title}</h2>
                    <button type="button" className="icon-button" aria-label="Close" onClick={onClose}>
                        <Icon name="close" size={15}/>
                    </button>
                </header>
                <div className="modal-body">{children}</div>
                {footer && <footer>{footer}</footer>}
            </div>
        </div>
    )
}

export type MenuItem = {label: string; icon?: IconName; onClick: () => void; danger?: boolean; disabled?: boolean} | 'separator'

/** A right-click menu at the pointer. */
export function ContextMenu({x, y, items, onClose}: {x: number; y: number; items: MenuItem[]; onClose: () => void}) {
    const root = useRef<HTMLDivElement>(null)
    useEffect(() => {
        const close = (event: MouseEvent | KeyboardEvent) => {
            if (event instanceof KeyboardEvent ? event.key === 'Escape' : !root.current?.contains(event.target as Node)) onClose()
        }
        document.addEventListener('mousedown', close)
        document.addEventListener('keydown', close)
        window.addEventListener('blur', onClose)
        return () => {
            document.removeEventListener('mousedown', close)
            document.removeEventListener('keydown', close)
            window.removeEventListener('blur', onClose)
        }
    }, [onClose])

    // Keep the menu on screen.
    const left = Math.min(x, window.innerWidth - 220)
    const top = Math.min(y, window.innerHeight - items.length * 32 - 16)
    return (
        <div ref={root} className="context-menu" role="menu" style={{left, top}}>
            {items.map((item, index) => item === 'separator'
                ? <hr key={index}/>
                : (
                    <button
                        key={item.label}
                        type="button"
                        role="menuitem"
                        className={item.danger ? 'danger' : ''}
                        disabled={item.disabled}
                        onClick={() => {
                            onClose()
                            item.onClick()
                        }}
                    >
                        {item.icon && <Icon name={item.icon} size={14}/>}
                        {item.label}
                    </button>
                ))}
        </div>
    )
}
