import {useMemo, useState} from 'react'
import {useSession} from '../../app/session'
import {useWorkspace} from '../../app/workspace'
import {Avatar} from '../../components/Avatar'
import {EmptyState} from '../../components/EmptyState'
import {Icon} from '../../components/Icon'
import {SplitPane} from '../../components/SplitPane'
import {Banner, PaneHeader} from '../../components/ui'
import {VirtualList} from '../../components/VirtualList'
import {call, hasDesktopHost, host, type HostSchemas} from '../../host/client'
import {useHostEvents, useHostQuery} from '../../host/useHost'
import {fileName, folderOf, formatDateTime, formatRelativeTime, formatRevision, plural} from '../../lib/format'

type Revision = HostSchemas['RevisionDto']
const ROW_HEIGHT = 56

/** The branch's revisions, newest first, with the selected one's files on the right. */
export function HistoryPage() {
    const {project, branch} = useSession()
    const {status} = useWorkspace()
    const [reload, setReload] = useState(0)
    const [query, setQuery] = useState('')
    const [selected, setSelected] = useState<number>()

    const revisions = useHostQuery(
        signal => project && branch
            ? call(host.GET('/api/projects/{projectId}/branches/{branchId}/revisions', {
                params: {path: {projectId: project.id, branchId: branch.id}, query: {limit: 500}},
                signal,
            }))
            : Promise.resolve([]),
        [project?.id, branch?.id, reload],
    )

    // A new revision on the branch shows up without a refresh.
    useHostEvents(event => {
        if (event.type === 'workspace.activity' && (event.data as {kind?: string}).kind === 'Submitted') setReload(n => n + 1)
        if (event.type === 'operation.done' && (event.data as {kind?: string}).kind === 'submit') setReload(n => n + 1)
    })

    const needle = query.trim().toLocaleLowerCase()
    const visible = useMemo(() => (revisions.data ?? []).filter(r =>
        needle.length === 0 ||
        r.message.toLocaleLowerCase().includes(needle) ||
        r.username.toLocaleLowerCase().includes(needle) ||
        (r.displayName ?? '').toLocaleLowerCase().includes(needle) ||
        formatRevision(r.number).includes(needle)), [revisions.data, needle])

    const revision = visible.find(r => r.number === selected) ?? visible[0]
    const synced = status?.syncedRevision ?? 0

    if (!hasDesktopHost()) return <EmptyState icon="history" title="History is in the desktop app">Open Vertex Desktop to browse revisions.</EmptyState>

    const list = (
        <div className="history-pane">
            <PaneHeader title={branch ? `History of ${branch.name}` : 'History'} count={revisions.data?.length}>
                <button type="button" className="icon-button" title="Refresh" aria-label="Refresh" onClick={() => setReload(n => n + 1)} disabled={!branch}>
                    <Icon name="refresh" size={15}/>
                </button>
            </PaneHeader>
            <div className="changes-toolbar">
                <label className="filter">
                    <Icon name="search" size={13}/>
                    <input value={query} onChange={e => setQuery(e.target.value)} placeholder="Search descriptions, people or #revisions"/>
                </label>
            </div>
            {revisions.error && <Banner tone="error">{revisions.error.message}</Banner>}
            <div className="changes-list">
                {!branch ? (
                    <EmptyState icon="branch" title="No branch">Open a workspace to see its history.</EmptyState>
                ) : revisions.loading && !revisions.data ? (
                    <EmptyState icon="clock" title="Loading revisions…"/>
                ) : visible.length === 0 ? (
                    <EmptyState icon="history" title={needle ? 'No matching revisions' : 'No revisions yet'}>
                        {needle ? 'Try another search.' : 'Submit files from the Changes page and they show up here.'}
                    </EmptyState>
                ) : (
                    <VirtualList items={visible} rowHeight={ROW_HEIGHT} label="Revisions" activeIndex={visible.indexOf(revision)} render={item => (
                        <RevisionRow revision={item} active={item.number === revision?.number} synced={synced} onSelect={() => setSelected(item.number)}/>
                    )} onKeyDown={event => {
                        const index = visible.indexOf(revision)
                        if (event.key === 'ArrowDown' && index < visible.length - 1) {
                            event.preventDefault()
                            setSelected(visible[index + 1].number)
                        }
                        if (event.key === 'ArrowUp' && index > 0) {
                            event.preventDefault()
                            setSelected(visible[index - 1].number)
                        }
                    }}/>
                )}
            </div>
        </div>
    )

    return <SplitPane id="history" list={list} detail={revision ? <RevisionDetail revision={revision} synced={synced}/> : <EmptyState icon="history" title="Select a revision"/>} initial={420}/>
}

function RevisionRow({revision, active, synced, onSelect}: {revision: Revision; active: boolean; synced: number; onSelect: () => void}) {
    const author = revision.displayName ?? revision.username
    const firstLine = revision.message.split('\n')[0]
    return (
        <button type="button" className={`revision-row ${active ? 'active' : ''}`} onClick={onSelect}>
            <Avatar person={{name: author}}/>
            <span>
                <b>{firstLine || <em>No description</em>}</b>
                <small>
                    {author} · <time title={formatDateTime(revision.createdAt)}>{formatRelativeTime(revision.createdAt)}</time> · {plural(revision.files, 'file')}
                </small>
            </span>
            <code className={revision.number > synced && synced > 0 ? 'new' : ''} title={revision.number > synced && synced > 0 ? 'Not synced to this workspace yet' : undefined}>
                {formatRevision(revision.number)}
            </code>
        </button>
    )
}

function RevisionDetail({revision, synced}: {revision: Revision; synced: number}) {
    const {project, branch} = useSession()
    const files = useHostQuery(
        signal => call(host.GET('/api/projects/{projectId}/branches/{branchId}/revisions/{number}/files', {
            params: {path: {projectId: project!.id, branchId: branch!.id, number: revision.number}},
            signal,
        })),
        [project?.id, branch?.id, revision.number],
    )
    const author = revision.displayName ?? revision.username

    return (
        <article className="detail">
            <header className="detail-header">
                <span className="detail-icon"><Icon name="commit" size={18}/></span>
                <div>
                    <h2>{formatRevision(revision.number)}</h2>
                    <p className="path">
                        <Avatar person={{name: author}}/> {author} · <time title={formatDateTime(revision.createdAt)}>{formatDateTime(revision.createdAt)}</time>
                    </p>
                </div>
            </header>
            {synced > 0 && revision.number > synced && <Banner tone="info" icon="download">Not in your workspace yet. Sync to get it.</Banner>}
            <p className="revision-message">{revision.message || <em className="muted">No description</em>}</p>
            <h3 className="section-title">{plural(revision.files, 'file')}</h3>
            <div className="revision-files">
                {files.error ? (
                    <p>{files.error.message}</p>
                ) : files.loading ? (
                    <p className="muted">Loading files…</p>
                ) : (
                    files.data?.map(file => (
                        <div key={file.path} className={file.deleted ? 'deleted' : ''}>
                            <span className={`change-badge ${file.deleted ? 'deleted' : 'changed'}`}>{file.deleted ? 'D' : '•'}</span>
                            <span>{fileName(file.path)}</span>
                            <small>{folderOf(file.path)}</small>
                        </div>
                    ))
                )}
            </div>
        </article>
    )
}
