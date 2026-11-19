import type {ReactNode} from 'react'
import {Icon, type IconName} from './Icon'

/** A centered message for a pane with nothing to show: not logged in, no project, an error, or still loading. */
export function EmptyState({icon, title, children}: {icon: IconName; title: string; children?: ReactNode}) {
    return (
        <div className="empty-state" role="status">
            <Icon name={icon} size={22}/>
            <b>{title}</b>
            {children && <p>{children}</p>}
        </div>
    )
}
