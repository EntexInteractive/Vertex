import {useSession} from '../../app/session'
import {useShell} from '../../app/shell'
import {useWorkspace} from '../../app/workspace'
import {Icon, type IconName} from '../../components/Icon'
import {Banner} from '../../components/ui'
import {formatRelativeTime} from '../../lib/format'

/** Shown when no workspace is open: get one (clone, open, create), or pick a recent one. */
export function WelcomePage() {
    const {session} = useSession()
    const {recent, open, browse, fail, statusError} = useWorkspace()
    const {openDialog} = useShell()
    const available = recent.filter(r => r.exists)

    const run = (action: () => Promise<void>) => {
        action().catch((error: unknown) => fail(error instanceof Error ? error.message : String(error)))
    }

    return (
        <div className="welcome">
            <div className="welcome-inner">
                <h1>Let's get you a workspace</h1>
                <p className="lead">
                    A workspace is a folder on this computer that holds a project's files. History stays on the server
                    {session?.server ? <> at <b>{session.server}</b></> : null}.
                </p>
                {statusError && <Banner tone="error">{statusError.message}</Banner>}
                <div className="welcome-actions">
                    <WelcomeCard icon="download" title="Clone a project" text="Get the files of a project that's already on the server." primary onClick={() => openDialog('clone')}/>
                    <WelcomeCard icon="folder" title="Open a workspace" text="A folder you cloned before, here or with vx." onClick={() => run(browse)}/>
                    <WelcomeCard icon="plus" title="New project" text="Turn a folder into a new project. Its files become your first submit." onClick={() => openDialog('new')}/>
                </div>
                {available.length > 0 && (
                    <section className="welcome-recent">
                        <h2>Recent</h2>
                        {available.map(item => (
                            <button key={item.root} type="button" onClick={() => run(() => open(item.root))}>
                                <Icon name="folder" size={16}/>
                                <span>
                                    <b>{item.project ?? item.name}</b>
                                    <small>{item.root}</small>
                                </span>
                                <em>{item.branch ?? 'main'}</em>
                                <time>{formatRelativeTime(item.lastOpened)}</time>
                            </button>
                        ))}
                    </section>
                )}
            </div>
        </div>
    )
}

function WelcomeCard({icon, title, text, onClick, primary}: {icon: IconName; title: string; text: string; onClick: () => void; primary?: boolean}) {
    return (
        <button type="button" className={`welcome-card ${primary ? 'primary' : ''}`} onClick={onClick}>
            <span className="welcome-icon"><Icon name={icon} size={20}/></span>
            <b>{title}</b>
            <small>{text}</small>
        </button>
    )
}
