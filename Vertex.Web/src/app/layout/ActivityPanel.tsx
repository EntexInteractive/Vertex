import {useEffect, useRef} from 'react'
import {Avatar} from '../../components/Avatar'
import {EmptyState} from '../../components/EmptyState'
import {Icon} from '../../components/Icon'
import {formatDateTime, formatRelativeTime, formatRevision, plural} from '../../lib/format'
import {type Activity, useWorkspace} from '../workspace'

/** What others did in the project since the app opened: locks taken and released, revisions submitted. */
export function ActivityPanel({onClose}: {onClose: () => void}) {
    const {activity, markActivityRead} = useWorkspace()
    const root = useRef<HTMLDivElement>(null)

    useEffect(() => {
        markActivityRead()
        const close = (event: MouseEvent | KeyboardEvent) => {
            if (event instanceof KeyboardEvent ? event.key === 'Escape' : !root.current?.parentElement?.contains(event.target as Node)) onClose()
        }
        document.addEventListener('mousedown', close)
        document.addEventListener('keydown', close)
        return () => {
            document.removeEventListener('mousedown', close)
            document.removeEventListener('keydown', close)
        }
        // Mark read once per opening.
        // eslint-disable-next-line react-hooks/exhaustive-deps
    }, [])

    return (
        <div className="activity-panel" ref={root} role="dialog" aria-label="Activity">
            <header>
                <h2>Activity</h2>
                <small>Locks and revisions by others on this branch</small>
            </header>
            <div className="activity-list">
                {activity.length === 0 ? (
                    <EmptyState icon="bell" title="Nothing yet">When someone locks a file or submits a revision, it shows up here.</EmptyState>
                ) : activity.map(item => <ActivityRow key={item.id} item={item}/>)}
            </div>
        </div>
    )
}

function ActivityRow({item}: {item: Activity}) {
    const who = item.sameUser ? `You (on ${item.host})` : item.username
    const first = item.paths[0]
    const more = item.pathCount > 1 ? ` and ${plural(item.pathCount - 1, 'other file')}` : ''
    const text = item.kind === 'Submitted'
        ? <>submitted <b>{formatRevision(item.revision)}</b>{item.message ? <> — {item.message}</> : null} ({plural(item.files, 'file')})</>
        : item.kind === 'Locked'
            ? <>locked <code>{first}</code>{more}</>
            : item.kind === 'Unlocked'
                ? <>released <code>{first}</code>{more}</>
                : <>holds <code>{first}</code>, which you changed. Wait for them, or revert your change.</>

    return (
        <div className={`activity-row ${item.kind === 'Conflict' ? 'conflict' : ''}`}>
            {item.kind === 'Conflict' ? <span className="activity-icon"><Icon name="alert" size={14}/></span> : <Avatar person={{name: item.username}}/>}
            <p>
                <b>{who}</b> {text}
                <small title={formatDateTime(item.at)}>{formatRelativeTime(item.at)}</small>
            </p>
        </div>
    )
}
