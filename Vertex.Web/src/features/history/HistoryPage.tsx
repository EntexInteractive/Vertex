import {useState} from 'react'
import {useSession} from '../../app/session'
import {Avatar} from '../../components/Avatar'
import {TableHead} from '../../components/DataTable'
import {EmptyState} from '../../components/EmptyState'
import {Icon} from '../../components/Icon'
import {ViewFrame} from '../../components/ViewFrame'
import {call, hasDesktopHost, host, type HostSchemas} from '../../host/client'
import {useHostQuery} from '../../host/useHost'
import {formatDateTime, formatRelativeTime, formatRevision, plural} from '../../lib/format'

type Revision = HostSchemas['RevisionDto']

/** The selected branch's submitted revisions, newest first. Click one to see the files it changed. */
export function HistoryPage() {
    const {project, branch} = useSession()
    const [reload, setReload] = useState(0)
    const [query, setQuery] = useState('')
    const [expanded, setExpanded] = useState<number>()

    const revisions = useHostQuery(
        signal => project && branch
            ? call(host.GET('/api/projects/{projectId}/branches/{branchId}/revisions', {
                params: {path: {projectId: project.id, branchId: branch.id}, query: {limit: 200}},
                signal,
            }))
            : Promise.resolve([]),
        [project?.id, branch?.id, reload],
    )

    const needle = query.trim().toLocaleLowerCase()
    const visible = (revisions.data ?? []).filter(r =>
        needle.length === 0 ||
        r.message.toLocaleLowerCase().includes(needle) ||
        r.username.toLocaleLowerCase().includes(needle) ||
        (r.displayName ?? '').toLocaleLowerCase().includes(needle) ||
        formatRevision(r.number).includes(needle))

    return (
        <ViewFrame
            eyebrow={project && branch ? `${project.name.toUpperCase()} / ${branch.name.toUpperCase()}` : 'HISTORY'}
            title="Submitted revisions"
            description="Every revision submitted to this branch, newest first."
            action={{label: 'Refresh', icon: 'refresh', onClick: () => setReload(n => n + 1), disabled: !branch}}
        >
            <div className="history-filter">
                <label>
                    <Icon name="search" size={14}/>
                    <input value={query} onChange={e => setQuery(e.target.value)} placeholder="Search descriptions, authors or #revisions"/>
                </label>
            </div>
            <TableHead cols={['REVISION', 'DESCRIPTION', 'FILES', 'AUTHOR', 'SUBMITTED']}/>
            <div className="data-table">
                {!hasDesktopHost() ? (
                    <EmptyState icon="history" title="History is in the desktop app">Open Vertex Desktop to browse revisions.</EmptyState>
                ) : revisions.error ? (
                    <EmptyState icon="history" title="Couldn't load history">{revisions.error.message}</EmptyState>
                ) : !branch ? (
                    <EmptyState icon="branch" title="No branch selected">Pick a project and branch at the top.</EmptyState>
                ) : revisions.loading ? (
                    <EmptyState icon="clock" title="Loading revisions…"/>
                ) : visible.length === 0 ? (
                    <EmptyState icon="history" title={needle ? 'No matching revisions' : 'No revisions yet'}>
                        {needle ? 'Try another search.' : "Submit files with 'vx submit' and they show up here."}
                    </EmptyState>
                ) : (
                    visible.map(revision => (
                        <RevisionRow
                            key={revision.number}
                            revision={revision}
                            expanded={expanded === revision.number}
                            onToggle={() => setExpanded(current => (current === revision.number ? undefined : revision.number))}
                        />
                    ))
                )}
            </div>
        </ViewFrame>
    )
}

function RevisionRow({revision, expanded, onToggle}: {revision: Revision; expanded: boolean; onToggle: () => void}) {
    const author = revision.displayName ?? revision.username
    return (
        <>
            <button type="button" className={`data-row history-data ${expanded ? 'expanded' : ''}`} aria-expanded={expanded} onClick={onToggle}>
                <code>
                    <Icon name="commit" size={14}/>
                    {formatRevision(revision.number)}
                </code>
                <span>{revision.message || <em>No description</em>}</span>
                <small>{plural(revision.files, 'file')}</small>
                <span>
                    <Avatar person={{name: author}}/>
                    {author}
                </span>
                <small title={formatDateTime(revision.createdAt)}>{formatRelativeTime(revision.createdAt)}</small>
            </button>
            {expanded && <RevisionFiles number={revision.number}/>}
        </>
    )
}

function RevisionFiles({number}: {number: number}) {
    const {project, branch} = useSession()
    const files = useHostQuery(
        signal => call(host.GET('/api/projects/{projectId}/branches/{branchId}/revisions/{number}/files', {
            params: {path: {projectId: project!.id, branchId: branch!.id, number}},
            signal,
        })),
        [project?.id, branch?.id, number],
    )

    return (
        <div className="revision-files">
            {files.error ? (
                <p>{files.error.message}</p>
            ) : files.loading ? (
                <p>Loading files…</p>
            ) : (
                files.data?.map(file => (
                    <div key={file.path} className={file.deleted ? 'deleted' : ''}>
                        <span className={`state ${file.deleted ? 'deleted' : 'modified'}`}>{file.deleted ? 'D' : '•'}</span>
                        <code>{file.path}</code>
                    </div>
                ))
            )}
        </div>
    )
}
