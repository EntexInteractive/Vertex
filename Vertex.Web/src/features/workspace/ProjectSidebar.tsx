import {useState} from 'react'
import {useSession} from '../../app/session'
import {Icon} from '../../components/Icon'

/** The left column of the Workspace page: the file tree (sample for now), the project's branches and the connection. */
export function ProjectSidebar() {
    const {session, project, projects, selectProject, branches, branch, selectBranch} = useSession()
    const [filter, setFilter] = useState('')
    const needle = filter.trim().toLocaleLowerCase()
    const visibleBranches = branches.filter(b => b.name.toLocaleLowerCase().includes(needle))
    const otherProjects = projects.filter(p => p.id !== project?.id && p.name.toLocaleLowerCase().includes(needle))

    return (
        <aside className="project-sidebar">
            <label className="side-search">
                <Icon name="search" size={14}/>
                <input value={filter} onChange={e => setFilter(e.target.value)} placeholder="Filter branches and projects"/>
            </label>
            <div className="tree-section">
                <p>
                    <Icon name="chevron" size={12}/> FOLDERS
                </p>
                <button type="button" disabled title="Coming with workspace support">
                    <Icon name="folder" size={15}/>
                    <span>{project?.name ?? 'No project'}</span>
                </button>
            </div>
            <div className="tree-section branches">
                <p>
                    <Icon name="chevron" size={12}/> BRANCHES
                </p>
                {visibleBranches.map(b => (
                    <button key={b.id} type="button" className={b.id === branch?.id ? 'active' : ''} onClick={() => selectBranch(b.id)}>
                        <Icon name="branch" size={14}/>
                        <span>{b.name}</span>
                    </button>
                ))}
            </div>
            {otherProjects.length > 0 && (
                <div className="tree-section">
                    <p>
                        <Icon name="chevron" size={12}/> OTHER PROJECTS
                    </p>
                    {otherProjects.map(p => (
                        <button key={p.id} type="button" onClick={() => selectProject(p.id)}>
                            <Icon name="archive" size={14}/>
                            <span>{p.name}</span>
                        </button>
                    ))}
                </div>
            )}
            <div className="workspace-state">
                <span>
                    <i className={session?.loggedIn ? '' : 'offline'}/>
                    {session?.loggedIn ? `Connected to ${new URL(session.server).host}` : 'Not connected'}
                </span>
                <small>{session?.username ? `Logged in as ${session.username}` : 'Not logged in'}</small>
            </div>
        </aside>
    )
}
